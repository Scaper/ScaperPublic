[<RequireQualifiedAccess>]
module LandUse

open System
open FSharp.Data
open System.Threading

// Parse land use data

type private ZoneCsv = CsvProvider<DataSources.zoneFile>
let private ZoneCaches = ZoneCsv.Load(DataSources.zoneFile)

let private getColumn (f:ZoneCsv.Row->'t) : 't[] = ZoneCaches.Rows |> Seq.map f |> Array.ofSeq

//Total population
let aLogPop = new ThreadLocal<_>(fun () -> getColumn (fun r -> float r.Pop |> max 1.0 |> log))
let aLogPop0 = aLogPop.Value

//Total employment
let aLogEmp = new ThreadLocal<_>(fun () -> getColumn (fun r -> float r.Emp |> max 1.0 |> log))
let aLogEmp0 = aLogEmp.Value

//Total parking rate
let aPRate = new ThreadLocal<_>(fun () -> getColumn (fun r -> (float r.Parkingrate)/60.0 )) //rate per minute
let aPRate0 = aPRate.Value


///The total number of zones the program has loaded
let N = aLogPop.Value.Length

///The total number of zones squared, i.e. the total number of O/D combinations in the zone system
let Nsq = N * N


///Converts from the provided zone number (listed in the input data) to the internal zone index (in [0, N-1]).
let zoneIndex = getColumn (fun r -> r.Id, r.Idx) |> dict

///Converts from the internal zone index (in [0, N-1]) to the provided zone number (listed in the input data).
let zoneNumber = getColumn (fun r -> r.Idx, r.Id) |> dict

//let zoneIndex = 
//    let c = Counter()
//    getColumn (fun r -> r.Zone_number, c.Next) |> dict

//let zoneNumber = 
//    let c = Counter()
//    getColumn (fun r -> c.Next, r.Zone_number) |> dict


///Prints a message about the zones (currently how many zones there are).
let printStats () = 
    printfn $"Loaded land use data with {N} zones"