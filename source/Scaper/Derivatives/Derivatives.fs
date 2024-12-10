module Derivatives

open System
open System.Threading
open FSharp.Collections.ParallelSeq
open CsvHelper.Configuration
open StateSpace
open Observations
open Parameters
open EVCache
open Matrix
open UtilityFuncs
open World
open ZoneSampling
open TravelDerivative
open ValueFunction

#nowarn "20"

///Origin location of the derivative we are interested in
let dOrig (agent:Agent) = Location.Residence agent.HomeZone

///Destination location of the derivative we are interested in
let dDest (agent:Agent) = Location.NonFixed None

///Mode of the derivative we are interested in
[<Literal>]
let dMode = Mode.Transit


///Whether to use the (exact) derivative of the approximated value function, instead of the (approximated) derivative of the true value function.
[<Literal>]
let UseDerivativeOfApproximateVF = false

///Record type for output of calculated EV derivatives
type EVDerivRecord = {
    IndID : int
    TravelTime : float
    WorkDuration : float
    EV : float
    dEV : float
    dEVNum : float
    dEvC : float
    conditionalUs : float[]
}

///Type provided to the CsvWriter to direct how to write out the derivative record
type EVDerivMap(tt:bool, wd:bool, numD:bool) =
    inherit ClassMap<EVDerivRecord>()
    do base.Map(fun r -> r.IndID)
       base.Map(fun r -> r.TravelTime).Name("Travel time").Ignore(not tt)
       base.Map(fun r -> r.WorkDuration).Name("Work duration").Ignore(not wd)
       base.Map(fun r -> r.EV)
       base.Map(fun r -> r.dEV).Name(if numD then "dEV (analytical)" else "dEV")
       base.Map(fun r -> r.dEVNum).Name("dEV (numerical)").Ignore(not numD)
       base.Map(fun r -> r.dEvC).Name("dEV cost")
       base.Map(fun r -> r.conditionalUs)
       ()

///Evaluates the numerical derivative
let numericalDeriv (evPool:EVPool) (matPool:MatPool) (addReward) (deriv:TTDerivative) (world:World) (agent:Agent) (ev0:float) =

    use evc = new EVCache(evPool, InfeasibleVal)
    let epsWorld = new EpsilonTTWorld(world, deriv)
    let ev1 = (matSpan (startState agent |> stateExpectedValue matPool evc addReward epsWorld agent))[0]
    (ev1 - ev0)/Epsilon


///Get the time-dependent value of travel savings for a particular action (defined by deriv) for an agent
let timeDependentVTTS (addReward) (deriv) (pool:MatPool) (agent:Agent) (world:World) (ev:EVCache) (dEv:EVCache) (pEv:EVCache) (cEv:EVCache) : float[] = 
    let N = 107
 
    let result = Array.zeroCreate<float> (N+1)
    let orig = dOrig agent
    let dest = dDest agent

    for t in 0 .. N do

        let s = {
            Activity = Activity.Depart
            Location = orig
            TimeOfDay = float t
            Duration = 0
            Vehicle = Vehicle.NoVehicle
            HasWorked = agent.WorkZone.HasValue && (not dest.HasZone || agent.WorkZone.Value <> dest.Zone.Value)
        }

        let dUs, cUs = 
            if UseDerivativeOfApproximateVF
            then DerivativeApproxVF.optionUtilitiesD pool addReward deriv ev dEv cEv world agent s 
                 |> List.unzip3 |> (fun (g, x, y) -> pool.retn g; x, y)
            else DerivativeTheoreticalVF.optionUtilitiesD pool addReward deriv ev dEv pEv cEv world agent s 
                 |> List.unzip3 |> (fun (g1, z, y) -> let x, g2 = List.unzip z
                                                      pool.retn g1; pool.retn g2
                                                      x, y)

        let predicate (dec:Decision) = match dec with
                                       | Travel(m, d) when m = dMode && d = dest -> true
                                       | _ -> false

        let i = options false agent world.Zones s 
                |> Seq.findIndex predicate

        result[t] <- (Span.sum (matSpan dUs[i])) / (Span.sum (matSpan cUs[i]))

        pool.retn dUs
        pool.retn cUs

    result

///Calculate the EV for an agent and generate output with information on the time-dependent VTTS
let calcEVandDeriv (parameters:Parameters) (deriv:TTDerivative) (evPool:EVPool) (matPool:MatPool) (world:World) (agent:Agent) (ev:EVCache) (c:int) (numDeriv:bool) (tt:float option, wd:int option) =
        
    //set up utility functions
    let addReward w s d mat = addImmediateUtility parameters agent w c mat (s, d)

    //set up deriv ev cache
    use dEv = new EVCache(evPool, 0.0)
    use pEv = new EVCache(evPool, 0.0)
    use cEv = new EVCache(evPool, 0.0)

    //calculate evs and return record
    let evs = 
        if UseDerivativeOfApproximateVF 
        then DerivativeApproxVF.stateExpectedValueD matPool addReward deriv ev dEv cEv world agent (startState agent)
        else DerivativeTheoreticalVF.stateExpectedValueD matPool addReward deriv ev dEv pEv cEv world agent (startState agent)
    
    //numerical derivative if requested
    let dEv0n = if numDeriv then numericalDeriv evPool matPool addReward deriv world agent ((matSpan evs[0])[0]) else 0.0
    
    //time-dependent VTTS
    let cond = timeDependentVTTS addReward deriv matPool agent world ev dEv pEv cEv
    //let cond = [| 0.0 |]

    //return a record for output
    let result = { 
        IndID = agent.IndID
        TravelTime = defaultArg tt 0.0
        WorkDuration = if wd.IsSome then (float wd.Value/TimeResolutionPerHour) else 0
        EV = (matSpan evs[0])[0]
        dEV = (matSpan evs[1])[0]
        dEVNum = dEv0n
        dEvC = (matSpan evs[2])[0]
        conditionalUs = cond 
    }

    matPool.retn evs

    result


///Simulate a large number of daypaths and record the mode distribution, total number of activities and number of work activities
let simModesAndWork (parameters:Parameters) (pool:MatPool) (agent:Agent) (world:World) (ev:EVCache) : Map<Mode,float> * float * float =

    let N = 10000 //how many paths to simulate?
    
    let modeCounts = Array.zeroCreate<float> (enumMaxVal<Mode>())
    let mutable nActs, nWorkActs = 0, 0
    for _ in 1 .. N do
        let _, path = Simulation.simulateDay parameters agent world pool [| ev |]
        for m in (path |> List.choose (fun (s, d) -> match s, d with 
                                                     | {Location = Residence _}, Travel(m, Workplace _)
                                                     | {Location = Workplace _}, Travel(m, Residence _) -> Some m
                                                     | _ -> None)) do 
            modeCounts[asVal m] <- modeCounts[asVal m] + 1.0
        let nwt = path |> List.sumBy (fun (_, d) -> match d with 
                                                    | Start(Activity.Work) -> 1
                                                    | _ -> 0)
        nWorkActs <- nWorkActs + nwt
        let na = path |> List.sumBy (fun (_, d) -> match d with
                                                   | Start(_) -> 1
                                                   | _ -> 0)
        nActs <- nActs + na
    let modeDist = normalizeSumTo1 modeCounts
    let modeDict = enumValues<Mode>() |> Array.map (fun m -> m, modeDist[asVal m]) |> Map.ofArray 

    modeDict, (float nWorkActs) / (float N), (float nActs) / (float N)



///Method which calculates the expected utility and start-of-day derivative for the provided agents.
let run (parameters:Parameters) (outputFile:string, degreeOfParallelism:int, takeMax:int option, zoneSampleSize:int option, ttRange:float[], wdRange:int[], numDeriv:bool, doSim:bool) = 

    let c = 0 //not using latent classes

    let agents = Observations.loadAgents None 
                 |> Seq.where(fun a -> a.WorkZone.HasValue && a.OwnsCar) 
                 |> Observations.truncateIfSome takeMax 
                 |> List.ofSeq

    printfn $"Calculating expected utilities and derivatives for {agents.Length} observations"
    if zoneSampleSize.IsSome then
        printfn $"Using zone sampling with {zoneSampleSize.Value} zones"
    else
        printfn $"Using all {LandUse.N} zones with no sampling"
    printfn $"Using up to {degreeOfParallelism} threads for execution"
    

    //figure out what we should be running
    let inputs = 
        match ttRange, wdRange with
        | [| minTT; deltaTT; maxTT |], [| minWD; deltaWD; maxWD |] -> 
            printfn $"\nMarginal utility calcs: varying both h<->w travel time (from {minTT:g} min to {maxTT:g} min by {deltaTT:g} min) and work duration (from {minWD*int TimeResolutionMinutes} min to {maxWD*int TimeResolutionMinutes} min by {deltaWD * int TimeResolutionMinutes} min)\n"
            List.allPairs [ minTT .. deltaTT .. maxTT ] [ minWD .. deltaWD .. maxWD ] 
            |> List.allPairs agents 
            |> List.map (fun (a, (t, d)) -> a, Some t, Some d)
        
        | [| minTT; deltaTT; maxTT |], [| |] -> 
            printfn $"\nMarginal utility calcs: varying h<->w travel time (from {minTT:g} min to {maxTT:g} min by {deltaTT:g} min)\n"
            List.allPairs agents [ minTT .. deltaTT .. maxTT ] 
            |> List.map (fun (a, t) -> a, Some t, None)
        
        | [| |], [| minWD; deltaWD; maxWD |] -> 
            printfn $"\nMarginal utility calcs: varying work duration (from {minWD*int TimeResolutionMinutes} min to {maxWD*int TimeResolutionMinutes} min by {deltaWD * int TimeResolutionMinutes} min)\n"
            List.allPairs agents [ minWD .. deltaWD .. maxWD ] 
            |> List.map (fun (a, d) -> a, None, Some d)
        
        | [| |], [| |] -> 
            printfn "\nMarginal utility calcs: running original h<->w travel time with flexible work duration\n"
            agents |> List.map (fun a -> a, None, None)
        
        | _ -> raise(ArgumentException("Poorly formed input to udelta command. Options -tt and -wd, if used, should each have zero or three numbers as inputs."))
    
    //make array pools
    //create the various object/array pools
    let nZones = if zoneSampleSize.IsSome then zoneSampleSize.Value else LandUse.N
    let matPool = new ThreadLocal<_>(fun () -> MatPool nZones)
    let evPool = new ThreadLocal<_>(fun () -> EVPool nZones)
    let worldPool = new ThreadLocal<_>(fun () -> new WorldPool(nZones))

    //make logger
    let logger = Progress.Logger(inputs.Length, degreeOfParallelism)
    use csv = DataSources.getSimCsvWriter outputFile
    
    let _, tt, wd = inputs.Head
    EVDerivMap(tt.IsSome, wd.IsSome, numDeriv) |> csv.Context.RegisterClassMap

    let modes = enumValues<Mode>()

    //write headers
    csv.WriteHeader<EVDerivRecord>()
    if doSim then
        for m in modes do csv.WriteField $"P({m})"
        csv.WriteField "Avg work activities"
        csv.WriteField "Avg total activities"
    csv.NextRecord()

    //function to perform the needed calculations and write output to CSV
    let runAndWriteCsv (agent:Agent, tt:float option, wd:int option) =
        
        //modify travel times
        let oldTTs = Array.zeroCreate<float> 2
        if tt.IsSome && agent.WorkZone.HasValue then
            for peak in [false; true] do
                oldTTs[asInt peak] <- Network.TTMap.Value[Mode.Car, peak, false][LandUse.N*agent.HomeZone + agent.WorkZone.Value]
                Network.TTMap.Value[Mode.Car, peak, false][LandUse.N*agent.HomeZone + agent.WorkZone.Value] <- tt.Value
                Network.TTMap.Value[Mode.Car, peak, true][LandUse.N*agent.WorkZone.Value + agent.HomeZone] <- tt.Value

        agent.WorkDuration <- Option.toNullable wd
        use ev = new EVCache(evPool.Value, InfeasibleVal)
        use world = match zoneSampleSize with
                    | Some N -> 
                        let reqZones = if agent.WorkZone.HasValue then [| agent.HomeZone; agent.WorkZone.Value |] else [| agent.HomeZone |]                            
                        zoneImportanceSample worldPool.Value agent N reqZones
                    | None -> fullWorld()

        let dTTdx = makeDeriv world dMode (dOrig agent) (dDest agent)

        try
            //calculate the ev and write the record
            let record = calcEVandDeriv parameters dTTdx evPool.Value matPool.Value world agent ev c numDeriv (tt, wd)

            //do the simulation if requested
            let modeDist, nWork, nActs = if doSim then simModesAndWork parameters matPool.Value agent world ev else Map.empty, 0.0, 0.0

            logger.Result true
            
            let write() = 
                csv.WriteRecord record
                if doSim then                
                    for m in modes do csv.WriteField<float> modeDist[m]
                    csv.WriteField<float> nWork
                    csv.WriteField<float> nActs
                csv.NextRecord()
                csv.Flush()

            lock csv write

            //change back travel times to clean up for the next agent
            if tt.IsSome && agent.WorkZone.HasValue then
                for peak in [false; true] do
                    Network.TTMap.Value[Mode.Car, peak, false][LandUse.N*agent.HomeZone + agent.WorkZone.Value] <- oldTTs[asInt peak]
                    Network.TTMap.Value[Mode.Car, peak, true][LandUse.N*agent.WorkZone.Value + agent.HomeZone] <- oldTTs[asInt peak]

        with
        | e -> Console.Error.WriteLine e
               logger.Result false


    //do the calcultion and save to csv
    inputs
    |> PSeq.withDegreeOfParallelism degreeOfParallelism 
    |> PSeq.iter runAndWriteCsv

    