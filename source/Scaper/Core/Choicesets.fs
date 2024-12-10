module Choicesets

open System
open System.IO
open System.Threading
open System.Collections
open FSharp.Collections.ParallelSeq
open Parquet.Serialization
open Simulation
open World
open Observations
open ZoneSampling
open StateSpace
open TripConversion
open Parameters
open UtilityFuncs
open ValueFunction
open EVCache
open Matrix

///An element of a choiceset representing a day path the agent could have taken
type Alternative(trips:Trip seq, correction:float) =
    member val Trips = Generic.List<Trip>(trips) with get, set    
    member val Correction = correction with get, set
        
    override x.GetHashCode() = hash x.Trips
    override x.Equals(other) =
        match other with
        | :? Alternative as y -> (Seq.compareWith (fun t1 t2 -> if t1.Equals(t2) then 0 else 1) x.Trips y.Trips) = 0
        | _ -> false
        
    //Default constructor for serialization
    new() = Alternative([], 0.0)


///A choiceset for estimation. The observed alternative should be the 
///first element of Alternatives.
type Choiceset(agent:Agent, zones:int[], alternatives:Alternative seq) = 
    member val Agent = agent with get, set
    member val Zones = zones with get, set 
    member val Alternatives = Generic.List<Alternative>(alternatives) with get, set
    
    new() = Choiceset(Agent(), [||], [])


///Correction term for the daypath
let logProbability (ps:Parameters) (agent:Agent) (world:World) (ev0s:float[]) (daypath:DayPath)  = 
    
    let condProb (c:int) =
        let mat = [| -ev0s[c] |] |> ArraySegment
        daypath |> List.iter (fun sd -> addUtility ps (1.0, Shape.Scalar, mat) (Utility.decisionVariables agent world c sd)  |> ignore)
        exp mat[0]

    let classProbs = latentClassProbabilities ps agent
    let conditionalProbs = Array.init ps.nClasses condProb

    Seq.map2 (*) classProbs conditionalProbs |> Seq.sum |> log



///Create a choiceset for the given individual with the given size. The given observed
///trip list will be the first element of the choiceset's alternative list.
let makeChoiceset (size:int) (ps:Parameters) ((agent, obsTrips):Observation) (matPool:MatPool) (evPool:EVPool) (worldPool:WorldPool) (zoneSampleSize:int option) : Choiceset option =
    
    //zones which must be in the zone sample
    let reqZones = obsTrips 
                   |> List.map (fun t -> t.Destination) 
                   |> Set.ofList |> Set.toArray

    //zone sample
    use world = match zoneSampleSize with
                | Some N -> zoneImportanceSample worldPool agent N reqZones
                | None -> fullWorld()

    match tripListToDayPath agent obsTrips with
    | None -> None
    | Some obsDayPath -> 
         
         
        //array for evs
        use evs = Array.init ps.nClasses (fun _ -> new EVCache(evPool, InfeasibleVal)) |> makeDisposable
        let getEV c = 
            let addReward w s d var = addImmediateUtility ps agent w c var (s,d)
            let _,_,v = startState agent |> stateExpectedValue matPool evs.Items[c] addReward world agent 
            v[0]
        let ev0s = Array.init ps.nClasses getEV

        //total log probability of a daypath averaged across classes
        let correction daypath = -(logProbability ps agent world ev0s daypath)

        //observed alternative
        let obsAlt = Alternative(obsTrips, correction obsDayPath)
        
        match (System.Double.IsFinite obsAlt.Correction) with
        | false -> 
            Console.Error.WriteLine $"Observed daypath for agent {agent.IndID} not feasible according to model."
            None
        | true -> 
            
            let makeAlt _ = 
                try
                    let lc, path = simulateDay ps agent world matPool evs
                    let trips = dayPathToTripList agent (lc, path)
                    Some (Alternative(trips, correction path))
                with 
                | e -> 
                    Console.Error.WriteLine e
                    None

            let alts = List.init size makeAlt |> List.choose id

            //observed and list of alternatives, consolidate same alternatives
            let altList = obsAlt :: alts
                          |> List.countBy id 
                          |> List.map (fun (alt, n) -> Alternative(alt.Trips, alt.Correction + log(float n)))
            
            Some(Choiceset(agent, world.Zones, altList))




///Creates choicesets given an observation list
let choicesetsFromObs (obs:Observation list) (ps:Parameters) (nAlternatives:int, degreeOfParallelism:int, zoneSampleSize:int option) =

    printfn $"Creating choicesets for {obs.Length} observations"
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
    let logger = Progress.Logger(obs.Length, degreeOfParallelism)

    let createChoiceset obs = 
        let cs = makeChoiceset nAlternatives ps obs matPool.Value evPool.Value worldPool.Value zoneSampleSize
        logger.Result cs.IsSome
        cs

    obs
    |> PSeq.withDegreeOfParallelism degreeOfParallelism
    |> PSeq.choose createChoiceset


///Creates the choicesets with the given arguments
let run (ps:Parameters) (outputFile:string, nAlternatives:int, degreeOfParallelism:int, takeMax:int option, zoneSampleSize:int option) =

    let obs = Observations.loadObservations takeMax |> List.ofSeq

    let outPath = Path.Combine(DataSources.CsOutputFolder, Path.ChangeExtension(outputFile, "parquet"))
    Directory.CreateDirectory(DataSources.CsOutputFolder) |> ignore

    let results = choicesetsFromObs obs ps (nAlternatives, degreeOfParallelism, zoneSampleSize)
                  |> PSeq.toList
    
    printf "Serializing choicesets to parquet... "
    
    ParquetSerializer.SerializeAsync<Choiceset>(results, outPath) 
    |> Async.AwaitTask |> Async.RunSynchronously |> ignore
                 
    printfn "done!"
    