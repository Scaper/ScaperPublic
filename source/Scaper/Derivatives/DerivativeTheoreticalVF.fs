module DerivativeTheoreticalVF

open MKLNET
open World
open EVCache
open StateSpace
open Observations
open Matrix
open UtilityFuncs
open TravelDerivative
open ValueFunction



///Calculates sums of utilities and derivatives and caches them in the given EVCaches
let private sumAndCacheUs (pool:MatPool) (evs:EVCache) (dEvs:EVCache) (pEvs:EVCache) (cEvs:EVCache) (s:State) (usAndDerivs:(Mat*(Mat*Mat)*Mat) list) = 

    let utilities, tDerivs, cDerivs = List.unzip3 usAndDerivs
    let derivs, partials = List.unzip tDerivs

    //sum utilities appropriately
    let uSum = decisionMatShape s Decision.Continue |> pool.rent
    addMatrices uSum utilities |> ignore //keep the sum for finding probabilities for the derivative

    let dSum = decisionMatShape s Decision.Continue |> pool.rent
    let pSum = decisionMatShape s Decision.Continue |> pool.rent
    let cSum = decisionMatShape s Decision.Continue |> pool.rent

    //DERIVATIVES
    //if uSum is zero this is a zero-probability state so don't bother with derivatives
    if Span.sum (matSpan uSum) > 0 then

        //normalize utilities matrices by row to make probability distributions by origin (in place)
        List.iter (fun u -> divideIgnoreZeroDenoms u uSum) utilities

        //multiply normalized utilities matrices by derivatives for weight (in place)
        for i in 0 .. derivs.Length-1 do
            Vml.Mul(matSpan utilities[i], matSpan derivs[i], matSpan derivs[i])
            Vml.Mul(matSpan utilities[i], matSpan partials[i], matSpan partials[i])
            Vml.Mul(matSpan utilities[i], matSpan cDerivs[i], matSpan cDerivs[i])

        //sum weighted derivative matrices by row and cache
        addMatrices dSum derivs |> dEvs.cache s
        addMatrices pSum partials |> pEvs.cache s
        addMatrices cSum cDerivs |> cEvs.cache s
                
    //finish calculating expected utility and cache
    uSum |> logInPlace |> evs.cache s

    //un-rent the utility and sum matrices
    pool.retn utilities
    pool.retn derivs
    pool.retn partials
    pool.retn cDerivs
    pool.retn pSum

    [ uSum; dSum; cSum ]


///Using the value function, calculates the expected value and derivative using an approximation to the
///real derivative of the theoretical (un-approximated) value function, i.e. keeps track of the impact of
///start time changes and adds these partial derivatives where appropriate.
let optionUtilitiesD (pool:MatPool) (addReward:World->State->Decision->Mat->Mat) 
                     (deriv:TTDerivative) (evs:EVCache) (dEvs:EVCache) (pEvs:EVCache) (cEvs:EVCache)
                     (world:World) (agent:Agent) (state:State) : (Mat*(Mat*Mat)*Mat) list =

    use epsCWorld = new EpsilonTCWorld(world, deriv)
    
    let travelCostDerivative (s:State) (d:Decision) = 
        match d with
        | Travel(_) ->
            numericalRewardDerivative pool addReward world epsCWorld s s d
        | _ -> decisionMatShape s d |> pool.rent

    use epsTWorld = new EpsilonTTWorld(world, deriv)
       
    let travelTimeDerivative (s:State) (d:Decision) = 
        match d with
        | Travel(_) ->
            numericalRewardDerivative pool addReward world epsTWorld s s d
        | _ -> decisionMatShape s d |> pool.rent

    let startTimeDerivative (s:State) (d:Decision) = 
        let s1 = { s with TimeOfDay = s.TimeOfDay + Epsilon/DecisionStepMinutes }
        numericalRewardDerivative pool addReward world world s s1 d 

    let addFutureSTPartials (pEv:Mat) (s:State) (d:Decision) (utility:Mat) =
        match d with
        | Travel(mode, dest) ->
            let dtdx = deriv(mode, s.Location, dest)        //get the appropriate derivative
            let u = decisionMatShape s d |> pool.rent       //get an empty matrix to multiply into
            Vml.Mul(dtdx, matSpan pEv, matSpan u)                   //multiply the tt derivative by the start time partial
            addMatrices utility [ u ] |> ignore             //add to utility
            pool.retn u
        | _ -> ()
        utility

    let expectedUsAndDerivs (s:State) (d:Decision) (nextStates:State list) : Mat*(Mat*Mat)*Mat =

        //add immediate and expected utility and exponentiate
        //get the relevant expected values in the right format
        let evpart = evs.getAllTimesteps nextStates.Head
        let ev = decisionMatShape s d |> pool.rent
                 |> addReward world s d
                 |> addExpectedUtility world pool s d evpart
                 |> expInPlace

        //start time partial derivatives
        let pEvpart = pEvs.getAllTimesteps nextStates.Head
        let pEv = decisionMatShape s d |> pool.rent
                  |> addExpectedUtility world pool s d pEvpart

        //full derivatives assuming no change to start time
        let dEvpart = dEvs.getAllTimesteps nextStates.Head
        let dEv = travelTimeDerivative s d
                  |> addFutureSTPartials pEv s d
                  |> addExpectedUtility world pool s d dEvpart
        
        let pEv2 = startTimeDerivative s d
        addMatrices pEv [pEv2] |> ignore
        pool.retn pEv2

        let cEVpart = cEvs.getAllTimesteps nextStates.Head
        let cEv = travelCostDerivative s d
                  |> addExpectedUtility world pool s d cEVpart

        //return utility, derivative and partial
        ev, (dEv, pEv), cEv
    
    let sumGoodState s = sumAndCacheUs pool evs dEvs pEvs cEvs s >> pool.retn

    valueFunction expectedUsAndDerivs sumGoodState evs.needsCaching evs.cacheZero ignore world agent state 
    
    
///Gets the expected value (aka value function) of a state and its derivatives
let stateExpectedValueD (pool:MatPool) (addReward:World->State->Decision->Mat->Mat) 
                        (deriv:TTDerivative) (evs:EVCache) (dEvs:EVCache) (pEvs:EVCache) (cEvs:EVCache)
                        (world:World) (agent:Agent) (state:State) =

    optionUtilitiesD pool addReward deriv evs dEvs pEvs cEvs world agent state 
    |> sumAndCacheUs pool evs dEvs pEvs cEvs state