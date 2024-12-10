[<RequireQualifiedAccess>]
module Network


open System
open System.IO
open System.Threading
open Parquet
open Matrix

// *** LOAD NETWORK *** //

///Missing values or values less than this value in the data will be replaced with this value
[<Literal>]
let private minDataValue = 1.0

type PeakFlag = bool
type TransposeFlag = bool

let zeroArr = Array.zeroCreate<float> LandUse.Nsq

///Creates a map for a given level of service aspect (currently travel time, wait time, cost) 
///which differentiates based on mode and peak/off-peak
let makeLosMap (columns:Mode*PeakFlag->Map<string,float->float>) =

    use fileStream = File.OpenRead(DataSources.networkFile)
    use parquetReader = ParquetReader.CreateAsync fileStream |> Async.AwaitTask |> Async.RunSynchronously
    use groupReader = parquetReader.OpenRowGroupReader(0)
    let dataFields = parquetReader.Schema.GetDataFields() |> Array.map (fun df -> (df.Name, df)) |> Map.ofArray
    
    let col (name:string) (transformation:float->float) =        
        let col = groupReader.ReadColumnAsync dataFields[name] |> Async.AwaitTask |> Async.RunSynchronously
        let values = col.Data :?> Nullable<float>[] |> Array.map (fun l -> if l.HasValue then max l.Value minDataValue else minDataValue)
        Array.map transformation values
    
    let makeMatrix (mode:Mode, peak:PeakFlag) =
        (mode, peak, false), 
        (columns (mode, peak) |> Map.fold (fun s name transformation -> Array.map2 (+) s (col name transformation)) zeroArr)

    let los = Array.allPairs (enumValues<Mode>()) [| false; true |] |> Array.map makeMatrix

    let transposes = los |> Array.map (fun ((m,p,_),mat) -> (m,p,true), transposeNew mat LandUse.N LandUse.N)

    Array.concat [los; transposes] |> Map


let private ttColumns (mode:Mode,peak:PeakFlag) : Map<string,float->float> =
    match mode, peak with
    | Mode.Car, false -> Map ["midday_car_travel_times", fun ctt -> max (ctt/60.0) 1.0]
    | Mode.Car, true -> Map["mpeak_car_travel_times", fun ctt -> max (ctt/60.0) 1.0]
    | Mode.Transit, false -> Map ["midday_pt_travel_times",  fun ptt -> max (min (ptt/60.0) 120.0) 1]
    | Mode.Transit, true -> Map["mpeak_pt_travel_times",  fun ptt -> max (min (ptt/60.0) 120.0) 1]
    | Mode.Walk, _ -> Map["beeline_distances", fun wd -> max (15.0 * wd / 1000.0) 15]
    | Mode.Bike, _ -> Map["beeline_distances", fun bd -> max (4.0 * bd / 1000.0) 4]
    | _ -> Map.empty

    
///The OD travel time matrices by mode and peak hour. Thread local so when not sampling they can 
///be accessed in parallel without conflict.
let TTMap = new ThreadLocal<_>(fun() -> makeLosMap ttColumns)

///An accessor for a particular thread's OD travel time matrix, for use when sampling to avoid
///unnecessary duplication of the larger dataset across threads.
let TTMap0 = TTMap.Value


// *** TRANSIT WAIT TIME *** //

//Wait time for transit includes: up to 15 minutes of the first waiting period (assuming on routes with
//larger headways there will be some scheduling done) and all of the remainder of total wait time

let private waitColumns (mode:Mode, peak:PeakFlag) = 
    match mode, peak with
    | Mode.Transit, false -> Map ["midday_pt_frequencies", fun wt -> min (max (30.0/wt) 1.0) 30]
    | Mode.Transit, true -> Map ["mpeak_pt_frequencies", fun wt -> min (max (30.0/wt) 1.0) 30]
    | _ -> Map.empty

///The OD travel wait time matrices by mode and peak hour. Thread local so when not sampling they can 
///be accessed in parallel without conflict. Only Transit is nonzero.
let TWaitMap = new ThreadLocal<_>(fun () -> makeLosMap waitColumns)

///An accessor for a particular thread's OD travel wait time matrix, for use when sampling to avoid
///unnecessary duplication of the larger dataset across threads.
let TWaitMap0 = TWaitMap.Value



// *** TRANSIT WALK ACCESS *** //

//Transit access is the time taken to get to the transit stop (assumed by walking)

let private walkAccessColumns (mode:Mode, peak:PeakFlag) = 
    match mode, peak with
    | Mode.Transit, false -> Map ["midday_pt_access_times",  fun wt -> min (max (wt/60.0) 1.0) 30]
    | Mode.Transit, true -> Map ["mpeak_pt_access_times",  fun wt -> min (max (wt/60.0) 1.0) 30]
    | _ -> Map.empty

///The OD travel access time matrices by mode and peak hour. Thread local so when not sampling they can 
///be accessed in parallel without conflict. Only Transit is nonzero.
let TAccessMap = new ThreadLocal<_>(fun () -> makeLosMap walkAccessColumns)

///An accessor for a particular thread's OD travel access time matrix, for use when sampling to avoid
///unnecessary duplication of the larger dataset across threads.
let TAccessMap0 = TAccessMap.Value




// *** TRAVEL COST *** //

let private costColumns (mode:Mode, peak:PeakFlag) =
    match mode, peak with
    | Mode.Car, false -> Map ["midday_car_distances", (*) 0.00153]
    | Mode.Car, true -> Map ["midday_car_distances", (*) 0.00153]
 //   | Mode.Transit, _ -> Map ["kontanttaxa_Samm", id]
    | _ -> Map.empty

///The OD travel cost matrices by mode and peak hour. Thread local so when not sampling they can 
///be accessed in parallel without conflict.
let TCMap = new ThreadLocal<_>(fun() -> makeLosMap costColumns)

///An accessor for a particular thread's OD travel cost matrix, for use when sampling to avoid
///unnecessary duplication of the larger dataset across threads.
let TCMap0 = TCMap.Value


// *** PEAK PERIOD DEFINITIONS *** //                   
[<Literal>]
let amPeakStart = (7.0 - DayStartHour) * TimeResolutionPerHour
[<Literal>]
let amPeakEnd = (9.0 - DayStartHour) * TimeResolutionPerHour
[<Literal>]
let pmPeakStart = (16.0 - DayStartHour) * TimeResolutionPerHour
[<Literal>]
let pmPeakEnd = (18.0 - DayStartHour) * TimeResolutionPerHour
[<Literal>]
let peakBuffer = 1.0 * TimeResolutionPerHour

//Continuous function to transition between off-peak (0) and peak (1) over the day
let proportionPeak (t:float) : float = 
    if t <= amPeakStart - peakBuffer then 0.0
    elif t < amPeakStart then (t - (amPeakStart - peakBuffer))/peakBuffer |> cosSmooth
    elif t <= amPeakEnd then 1.0
    elif t < amPeakEnd + peakBuffer then (amPeakEnd + peakBuffer - t)/peakBuffer |> cosSmooth
    elif t <= pmPeakStart - peakBuffer then 0.0
    elif t < pmPeakStart then (t - (pmPeakStart - peakBuffer))/peakBuffer |> cosSmooth
    elif t <= pmPeakEnd then 1.0
    elif t < pmPeakEnd + peakBuffer then (pmPeakEnd + peakBuffer - t)/peakBuffer |> cosSmooth
    else 0.0


// *** LEVEL OF SERVICE *** //
//get appropriate level of service array for peak or off-peak
let private los (N:int) (odArr:Map<(Mode*PeakFlag*TransposeFlag),float[]>) (mode:Mode, oIdx:int option, dIdx:int option, peak:bool) =
    match oIdx, dIdx with
    | Some o, Some d -> Shape.Scalar, ArraySegment(odArr[mode, peak, false], N*o + d, 1)
    | Some o, None -> Shape.OneOxDs, ArraySegment(odArr[mode, peak, false], N*o, N)
    | None, Some d -> Shape.OneDxOs, ArraySegment(odArr[mode, peak, true], N*d, N)
    | None, None -> Shape.ODMat, ArraySegment(odArr[mode, peak, false])
            
   
///Given a (possibly sampled) network LOS information (travel time or cost) and information about the current trip (mode, orig, dest and time),
///returns a Var sequence representing the levels of service for that trip
let levelOfService (N:int) (odArr:Map<(Mode*PeakFlag*TransposeFlag),float[]>) (mode:Mode, oIdx:int option, dIdx:int option, time:float) : Mat seq =

    //proportion of peak this time represents
    let p = match mode with
            | Mode.Car | Mode.Transit -> proportionPeak time
            | _ -> 0.0
            
    //return one or two vars depending on proportion of peak
    seq {
        if p > 0.0 then 
            let s, arr = los N odArr (mode, oIdx, dIdx, true)
            p, s, arr
        if p < 1.0 then 
            let s, arr = los N odArr (mode, oIdx, dIdx, false)
            1.0-p, s, arr
    }


let inline private scalarLos (losMap:Map<(Mode*PeakFlag*TransposeFlag),float[]>) (mode:Mode, oIdx:int, dIdx:int, time:float) =
    levelOfService LandUse.N losMap (mode, Some oIdx, Some dIdx, time)
    |> Seq.sumBy (fun (scale, _, arr) -> scale*arr[0])

///A convenience function to get a single travel time from the network
let scalarTravelTime = scalarLos TTMap0 

///A convenience function to get a single wait time from the network
let scalarWaitTime = scalarLos TWaitMap0

///A convenience function to get a single access time from the network
let scalarAccessTime = scalarLos TAccessMap0

///A convenience function to get a single travel cost from the network
let scalarTravelCost = scalarLos TCMap0

///Gets an array of integer timesteps representing the range of travel times between 
///origin and destination (where None indicates all zones). For use to produce all 
///potential next states from a given decision.
let travelTimesteps N ttArr wtArr atArr (mode:Mode, orig:int option, dest:int option) : int[] =
    let minMax (peak:bool) =
        let tts = Span.readOnly (los N ttArr (mode, orig, dest, peak) |> snd)
        let wts = Span.readOnly (los N wtArr (mode, orig, dest, peak) |> snd)
        let ats = Span.readOnly (los N atArr (mode, orig, dest, peak) |> snd)
        ifloor (Span.minSum tts wts ats |> timeFromMinutes), iceil (Span.maxSum tts wts ats |> timeFromMinutes)
    
    match mode with
    | Mode.Car | Mode.Transit ->
        let mn1, mx1 = minMax false
        let mn2, mx2 = minMax true
        [| (min mn1 mn2) .. (max mx1 mx2) |]
    | _ -> 
        let mn, mx = minMax false
        [| mn .. mx |]



///Prints a message about the network data that has been loaded (currently outputs 
///ranges for travel time and cost for all modes for peak and off-peak times).
let printStats () = 
    let tts = enumValues<Mode>() |> Array.map (fun mode -> $"{mode} [{(Array.min TTMap0[(mode, false, false)]):F1}, {(Array.max TTMap0[(mode, false, false)]) :F1}]")
    printfn "%s" ("Travel time ranges off-peak (min): " + String.Join(", ", tts))

    let tts_p = [| Mode.Car; Mode.Transit |] |> Array.map (fun mode -> $"{mode} [{(Array.min TTMap0[(mode, true, false)]):F1}, {(Array.max TTMap0[(mode, true, false)]) :F1}]")
    printfn "%s" ("Travel time ranges peak (min): " + String.Join(", ", tts_p))

    let tat = enumValues<Mode>() |> Array.map (fun mode -> $"{mode} [{(Array.min TAccessMap0[(mode, false, false)]):F1}, {(Array.max TAccessMap0[(mode, false, false)]) :F1}]")
    printfn "%s" ("Access time ranges off-peak (min): " + String.Join(", ", tat))

    let twt = enumValues<Mode>() |> Array.map (fun mode -> $"{mode} [{(Array.min TWaitMap0[(mode, false, false)]):F1}, {(Array.max TWaitMap0[(mode, false, false)]) :F1}]")
    printfn "%s" ("Wait time ranges off-peak (min): " + String.Join(", ", twt))

    let tcs = enumValues<Mode>() |> Array.map (fun mode -> $"{mode} [{(Array.min TCMap0[(mode, false, false)]):F1}, {(Array.max TCMap0[(mode, false, false)]) :F1}]")
    printfn "%s" ("Travel cost ranges off-peak (sek): " + String.Join(", ", tcs))

    let tcs_p = [| Mode.Car; Mode.Transit |] |> Array.map (fun mode -> $"{mode} [{(Array.min TCMap0[(mode, true, false)]):F1}, {(Array.max TCMap0[(mode, true, false)]) :F1}]")
    printfn "%s" ("Travel cost ranges peak (sek): " + String.Join(", ", tcs_p))

 