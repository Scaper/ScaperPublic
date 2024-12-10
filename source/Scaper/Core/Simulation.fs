module Simulation

open System
open System.Threading
open FSharp.Collections.ParallelSeq
open Parameters
open StateSpace
open World
open Observations
open ZoneSampling
open EVCache
open Matrix
open UtilityFuncs
open ValueFunction
open TripConversion


///Calculates the probabilities of this agent belonging to each latent class
let latentClassProbabilities (ps:Parameters) (agent:Agent) : float[] =
    Array.init ps.nClasses (fun c -> 
        let m = Array.zeroCreate<float> 1 |> ArraySegment
        addUtility ps (1.0, Shape.Scalar, m) (Utility.classVariables agent c) |> ignore
        exp m[0]) 
    |> normalizeSumTo1


    
///Simulates an agent's entire daypath starting with their starting state 
///and including the 'choice' of latent class
let simulateDay (ps:Parameters) (agent:Agent) (world:World) (pool:MatPool) (evs:EVCache[]) = 
    
    //'choose' latent class membership
    let lc = match ps.nClasses with
             | 1 -> 0
             | _ -> Span.chooseIndex (latentClassProbabilities ps agent)
    
    //define the reward function
    let addReward w s d var = addImmediateUtility ps agent w lc var (s,d)

    //Makes a decision of what to do in state s
    let makeDecision (state:State) : Decision * float =
        let uVars = optionUtilities pool evs[lc] addReward world agent state
        let uArrs = uVars |> List.map (fun (s, _, arr) -> if s <> 1.0 then Span.scaleInPlace arr s
                                                          arr)
        let uArr = Seq.concat uArrs |> Array.ofSeq  //turn into probability distribution        
        
        pool.retn uVars //vars are rented in expDecisionUtility
        if (Array.sum uArr) = 0 then raise(Exception "All options had zero probability in makeDecision. Should not be possible!")

        //randomly choose an option using the probability distribution
        let idx = Span.chooseIndex uArr
        let p = uArr[idx]
        options true agent world.Zones state |> Seq.item idx, p

    //Simulates an agent's path starting at the given state
    let simulateFromState state : DayPath = 
        let rec simFromState probs path s : DayPath * float list =
            match (agent, s) with 
            | EndState -> path, [ 1.0 ]
            | BadState -> raise(Exception "Bad state reached in simulateFromState. Should not be possible!")
            | GoodState -> 
                let d, p = makeDecision s 
                simFromState (p::probs) ((s, d)::path) (nextSingleState agent s d)
        simFromState list.Empty list.Empty state |> fst |> List.rev
    
    //return latent class and simulated path
    lc, startState agent |> simulateFromState 



///Simulates and outputs as a (parallel) observation sequence for writing out or passing on
let simulateAgents (agents:Agent list) (ps:Parameters) (degreeOfParallelism:int, zoneSampleSize:int option) = 

    printfn $"Simulating day paths for {agents.Length} observations"
    if zoneSampleSize.IsSome then
        printfn $"Using zone sampling with {zoneSampleSize.Value} zones"
    else
        printfn $"Using all {LandUse.N} zones with no sampling"
    printfn $"Using up to {degreeOfParallelism} threads for execution"

    //create the various object/array pools
    let nZones = if zoneSampleSize.IsSome then zoneSampleSize.Value else LandUse.N
    let matPool = new ThreadLocal<_>(fun () -> MatPool nZones)
    let evPool = new ThreadLocal<_>(fun () -> EVPool nZones)
    let worldPool = new ThreadLocal<_>(fun () -> new WorldPool(nZones))
    let logger = Progress.Logger(agents.Length, degreeOfParallelism)

    //The function to run for each agent to simulate the day
    let sim (agent:Agent) = 
        use world = match zoneSampleSize with
                    | Some N -> 
                        let reqZones = if agent.WorkZone.HasValue then [| agent.HomeZone; agent.WorkZone.Value |] else [| agent.HomeZone |]
                        zoneImportanceSample worldPool.Value agent N reqZones
                    | None -> fullWorld()
        
        let makeEvCache _ = new EVCache(evPool.Value, InfeasibleVal)
        use evs = Array.init ps.nClasses makeEvCache |> makeDisposable
        
        try    
            let trips = simulateDay ps agent world matPool.Value evs 
                        |> dayPathToTripList agent
            
            logger.Result true
            Some (Observation (agent, trips))
        with
        | e -> Console.Error.WriteLine e
               logger.Result false
               None
    
    //run the simulation in parallel
    agents |> PSeq.withDegreeOfParallelism degreeOfParallelism
           |> PSeq.choose sim


///Runs the simulation with the given arguments
let run (ps:Parameters) (outputFile:string, degreeOfParallelism:int, takeMax:int option, zoneSampleSize:int option) = 
        
    let agents = Observations.loadAgents takeMax |> List.ofSeq

    use csv = DataSources.getSimCsvWriter outputFile
    csv.WriteHeader<Trip>()
    csv.NextRecord()

    simulateAgents agents ps (degreeOfParallelism, zoneSampleSize)
    |> PSeq.collect snd
    |> PSeq.iter (fun trip -> lock csv (fun () -> csv.WriteRecord trip
                                                  csv.NextRecord()))


///Loads the observed day paths into the model structure then writes them to CSV in the output format
let obsToCsv (outputFile:string, takeMax:int option) = 
    use csv = DataSources.getSimCsvWriter outputFile
    Observations.loadObservations takeMax
    |> Seq.choose (fun (a, ts) -> tripListToDayPath a ts |> Option.map (fun dp -> (a, dp)))
    |> Seq.collect (fun (a, dp) -> dayPathToTripList a (0, dp))
    |> csv.WriteRecords