module ValueFunction

open FSharpx.Continuation
open StateSpace
open World
open Observations
open EVCache
open Matrix
open UtilityFuncs

/// The recursive algorithm at the heart of Scaper's mathematics. Calculates the total exponentiated utility for 
/// a decision, recursively calculating the expected utility of states that could be reached by that decision and 
/// the utility of decisions made from those states. Caches the expected state utility to speed up repeated calls.
let valueFunction (addDecisionUtilities:State->Decision->State list->'a) (sumAndCacheGoodState: State -> 'a list -> unit) 
                  (needsComputing:State->bool) (cacheEndState:State->unit) (cacheBadState:State->unit)
                  (world:World) (agent:Agent) (state:State) : 'a list =

    ///Exponentiated total utility of Decision d from State s
    let rec decisionExpU (s:State) (d:Decision) = cont {
        let nextStates = nextIntegralTimeStates agent world s d       
        let! _ = nextStates |> List.map stateEU |> sequence
        return addDecisionUtilities s d nextStates
    }

    ///Expected utility of State s 
    and stateEU (s:State) = cont {
        if needsComputing s then
            match (agent, s) with
            | EndState -> cacheEndState s
            | BadState -> cacheBadState s
            | GoodState -> 
                let! us = options false agent world.Zones s |> List.ofSeq |> List.map (decisionExpU s) |> sequence
                sumAndCacheGoodState s us
    }
    
    //function to run the inner utility functions with the given decision
    let expDU (d:Decision) = runCont (decisionExpU state d) id raise

    //gets the expected utility for each option
    options false agent world.Zones state |> Seq.map expDU |> List.ofSeq



///Function to broadcast sum the given utilities and cache them.
let private sumAndCacheUs (pool:MatPool) (evs:EVCache) (s:State) (utilities:Mat list) =
    let uSum = decisionMatShape s Decision.Continue |> pool.rent
    addMatrices uSum utilities |> logInPlace |> evs.cache s
    pool.retn utilities
    uSum


///Gets the total utilities of the options from a particular state, using the recursive value function.
let optionUtilities (pool:MatPool) (evs:EVCache) addReward world agent state = 

    ///Adds the immediate reward and expected utility (from UtilityFuncs)
    let addDecisionUs (s:State) (d:Decision) (nextStates:State list) : Mat = 
        
        let evpart = evs.getAllTimesteps nextStates.Head

        decisionMatShape s d |> pool.rent
        |> addReward world s d
        |> addExpectedUtility world pool s d evpart
        |> expInPlace

    ///Sums the list of utilities and caches the result
    let sumGoodState (s:State) (utilities:Mat list) =
        sumAndCacheUs pool evs s utilities |> pool.retn
    
    valueFunction addDecisionUs sumGoodState evs.needsCaching evs.cacheZero ignore world agent state
    


///Gets the expected value (also called the value function) of a state using the optionUtilities function.
let stateExpectedValue (pool:MatPool) (evs:EVCache) (addReward:World->State->Decision->Mat->Mat)
    (world:World) (agent:Agent) (state:State) : Mat = 
    
    optionUtilities pool evs addReward world agent state
    |> sumAndCacheUs pool evs state