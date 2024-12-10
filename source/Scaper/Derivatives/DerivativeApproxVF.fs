module DerivativeApproxVF

open System
open MKLNET
open World
open StateSpace
open Observations
open EVCache
open Matrix
open UtilityFuncs
open TravelDerivative
open ValueFunction


///Calculates sums of utilities and derivatives and caches them in the given EVCaches
let private sumAndCacheUs (pool:MatPool) (evs:EVCache) (dEvs:EVCache) (cEvs:EVCache) (s:State) (usAndDerivs:(Mat*Mat*Mat) list) = 

    let utilities, derivs, cDerivs = List.unzip3 usAndDerivs
    let shape = decisionMatShape s Decision.Continue 

    //sum utilities appropriately
    let uSum = pool.rent shape
    addMatrices uSum utilities |> ignore //keep the sum for finding probabilities for the derivative

    let dSum = pool.rent shape
    let cSum = pool.rent shape

    //DERIVATIVES
    //if uSum is zero this is a zero-probability state so don't bother with derivatives
    if Span.sum (matSpan uSum) > 0 then

        //normalize utilities matrices by row to make probability distributions by origin (in place)
        List.iter (fun u -> divideIgnoreZeroDenoms u uSum) utilities

        //multiply normalized utilities matrices by derivatives for weight (in place)
        for i in 0 .. derivs.Length-1 do
            Vml.Mul(matSpan utilities[i], matSpan derivs[i], matSpan derivs[i])
            Vml.Mul(matSpan utilities[i], matSpan cDerivs[i], matSpan cDerivs[i])

        //sum weighted derivative matrices by row and cache
        addMatrices dSum derivs |> dEvs.cache s
        addMatrices cSum cDerivs |> cEvs.cache s

    //finish calculating expected utility and cache
    uSum |> logInPlace |> evs.cache s

    //un-rent the utility and sum matrices
    pool.retn utilities
    pool.retn derivs
    pool.retn cDerivs

    [ uSum; dSum; cSum ]


///Using the value function, calculates the expected value and derivative of the approximated value function
///(i.e. using the difference between the EV of the next states rather than keeping track of the start time
///impact using the direct derivation.
let optionUtilitiesD (pool:MatPool) (addReward:World->State->Decision->Mat->Mat) 
                        (deriv:TTDerivative) (evs:EVCache) (dEvs:EVCache) (cEvs:EVCache)
                        (world:World) (agent:Agent) (state:State) : (Mat*Mat*Mat) list =
    
    use epsTWorld = new EpsilonTTWorld(world, deriv)
    let travelTimeDerivative (s:State) (d:Decision) = 
        match d with
        | Travel(_) ->
            numericalRewardDerivative pool addReward world epsTWorld s s d
        | _ -> decisionMatShape s d |> pool.rent

    use epsCWorld = new EpsilonTCWorld(world, deriv)
    let travelCostDerivative (s:State) (d:Decision) = 
        match d with
        | Travel(_) ->
            numericalRewardDerivative pool addReward world epsCWorld s s d
        | _ -> decisionMatShape s d |> pool.rent


    ///Adds the difference between the value function for the surrounding integral timesteps
    ///for the derivative of the (approximated) value function
    let addDiffUtility (ev:float[]) (u:Mat) (ts:Mat) (destArr:int[]) (flagArr:float[]) =

        let mutable i = 0
        let tsSpan : ReadOnlySpan<float> = matSpan ts
        let uSpan : Span<float> = matSpan u
        let evSpan : ReadOnlySpan<float> = ReadOnlySpan(ev)
        let destSpan : ReadOnlySpan<int> = ReadOnlySpan(destArr)
        let flagSpan : ReadOnlySpan<float> = ReadOnlySpan(flagArr)

        while i < uSpan.Length do
            if flagSpan[i] <> 0 then
                let ts = min tsSpan[i] DayLength
                let idx = destSpan[i] + int ts
                let ev0, ev1 = evSpan[idx], evSpan[idx+1]
                if Double.IsFinite(ev0) && Double.IsFinite(ev1) then
                    let diff = flagSpan[i]*(ev1 - ev0)/TimeResolutionMinutes
                    uSpan[i] <- uSpan[i] + diff
            i <- i+1


    ///Finds interpolated values for the EV and adds them to the 'utility' 2d array.
    let addExpectedUtilityDA (s:State) (decision:Decision) (evpart:float[]) (dEvpart:float[]) (utility:Mat) : Mat = 

        //Get the correct destination array to pass into the addEvUtility function
        let ts, destArr = getDestArr world pool s decision
    
        //Do the utility interpolated pack
        addEvUtility dEvpart utility ts destArr
    
        //if we are travelling, add the difference between the nearest integral timesteps
        match decision with 
        | Travel(mode, dest) ->
            deriv(mode, s.Location, dest)
            |> addDiffUtility evpart utility ts destArr 
        | _ -> ()
    
        //return the timestep 2d array
        pool.retn ts
    
        utility


    let expectedUsAndDerivs (s:State) (d:Decision) (nextStates:State list) : Mat*Mat*Mat =

        //add immediate and expected utility and exponentiate
        //get the relevant expected values in the right format
        let evpart = evs.getAllTimesteps nextStates.Head
        let ev = decisionMatShape s d |> pool.rent
                 |> addReward world s d
                 |> addExpectedUtility world pool s d evpart
                 |> expInPlace

        //full derivatives assuming no change to start time
        let dEvpart = dEvs.getAllTimesteps nextStates.Head
        let dEv = travelTimeDerivative s d
                  |> addExpectedUtilityDA s d evpart dEvpart
        
        let cEvpart = cEvs.getAllTimesteps nextStates.Head
        let cEv = travelCostDerivative s d 
                  |> addExpectedUtility world pool s d cEvpart

        //return utility, derivative and partial
        ev, dEv, cEv
    
    let sumGoodState s = sumAndCacheUs pool evs dEvs cEvs s >> pool.retn

    valueFunction expectedUsAndDerivs sumGoodState evs.needsCaching evs.cacheZero ignore world agent state 



    
///Gets the expected value (aka value function) of a state and its derivatives
let stateExpectedValueD (pool:MatPool) (addReward:World->State->Decision->Mat->Mat) 
                        (deriv:TTDerivative) (evs:EVCache) (dEvs:EVCache) (cEvs:EVCache) 
                        (world:World) (agent:Agent) (state:State) =

    optionUtilitiesD pool addReward deriv evs dEvs cEvs world agent state 
    |> sumAndCacheUs pool evs dEvs cEvs state