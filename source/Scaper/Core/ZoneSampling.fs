module ZoneSampling

open System.Threading
open System.IO
open Parameters
open World
open UtilityFuncs
open Matrix
open LogLikelihoodLC
open Observations
open Output

/// Loaded values for this parameter file
let private ps = DataSources.loadParameters Utility.ZoneSamplingParameterFile

let private world = baseWorld()

/// Sequence of variables (string-Mat pairs) that define the zone sampling MNL utility function.
let zoneSamplingVariables (a:Agent) (dest:Location) : Variable seq = 
    seq {
        "LogEmp",                   world.LogEmp dest
        "LogPop",                   world.LogPop dest
        if a.WorkZone.HasValue then
            "DistHome_Worker",      world.TravelTime (Mode.Walk, dest, Location.Residence a.HomeZone, 0)
            "DistWork_Worker",      world.TravelTime (Mode.Walk, dest, Location.Workplace a.WorkZone.Value, 0)
        else
            "DistHome_Nonworker",   world.TravelTime (Mode.Walk, dest, Location.Residence a.HomeZone, 0)
    }


/// Local cache of arrays for calculating zone probabilities. Cannot be rented from a MatPool
/// because it needs to be full-sized (the pool will be sized to sampleSize).
let private zoneUtility = new ThreadLocal<_>(fun () -> Array.zeroCreate<float> LandUse.N)

/// Creates a zone sample using importance sampling for the given agent with the given size.
/// Outputs are intended to be passed into World.sampledWorld. Correction function relies only
/// on the destination's probability of being sampled, but trips with the same origin and 
/// destination get zero correction (to avoid correction-hacking).
let zoneImportanceSample (pool:WorldPool) (agent:Agent) (sampleSize:int) (requiredZones:int[]) : World =

    //get and reset our utility array
    let u = zoneUtility.Value
    Span.fill u 0.0

    //get variables and add the utility, then exponentiate
    zoneSamplingVariables agent (Location.NonFixed None)
    |> addUtility ps (1.0, Shape.OneDxOs, u) 
    |> expInPlace |> ignore
    
    Span.normalizeInPlace u
    
    //perform sampling
    let zones = [|
        yield! requiredZones
        for _ in 1 .. (sampleSize - requiredZones.Length) ->
            Span.chooseIndex u
    |]

    //create correction matrix
    let corrections = pool.Take()
    for d in 0 .. (sampleSize-1) do
        let dCorr = -log(float sampleSize * u[zones[d]])
        for o in 0 .. (sampleSize-1) do
            if o <> d then
                corrections[sampleSize*o + d] <- dCorr

    new World(zones, Some(pool), Some(corrections))



/// Perform estimation of the zone sampling parameters. Uses the observations from DataSources
/// filtered to only include non-home/non-work trips.
let runEstimation (outputFile:string, takeMax:int option, degParallelism:int, numericalHessian:bool) =

    let ps = DataSources.loadParameters Utility.ZoneSamplingParameterFile

    let observations = loadObservations takeMax 
                       |> Seq.collect (fun (a, trips) -> [ for t in trips -> a, t ])
                       |> Seq.filter (fun (_, t) -> t.Activity <> Activity.Home && t.Activity <> Activity.Work) //only take non-home and non-work trips
                       |> Seq.toList

    printfn "\nLoading data for zone sampling model"

    ///Function to get variable values (appropriately modified so chosen options are first)
    ///to pass into the estimation data loading
    let makeDataForAgent ((agent:Agent,trip:Trip), _:int) = 
        (trip.Destination :: [0..trip.Destination-1] @ [trip.Destination+1 .. LandUse.N-1])   //trip destination first
        |> List.map (fun z -> zoneSamplingVariables agent (Location.NonFixed (Some z)), 0.0)       //get the zone sampling variables, 0.0 for no correction

    let log = Progress.Logger(observations.Length, degParallelism)

    //define cost function
    let costFunc = makeLCLLCostFunc ps observations (fun _ -> Seq.empty) makeDataForAgent (fun _ -> 1.0) degParallelism log.Result

    //starting param vector
    let startParams = Array.zeroCreate<float> ps.estCount

    printfn "\nEstimating zone sampling model parameters"


    //perform optimization
    let _, opt, invHest = BFGS.maximize costFunc startParams None
    let invH = if numericalHessian then None else Some(invHest)

    //write the parameters to file
    Directory.CreateDirectory(DataSources.ParamOutputFolder) |> ignore
    let outPath = Path.Combine(DataSources.ParamOutputFolder, Path.ChangeExtension(outputFile, ".csv"))
    DataSources.writeParamFile ps Utility.ZoneSamplingParameterFile outPath opt.position.Array
        
    //output the statistics
    printEstimationResult ps costFunc opt invH