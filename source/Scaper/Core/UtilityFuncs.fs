module UtilityFuncs

open System
open System.Threading
open System.Numerics
open System.Runtime.Intrinsics
open System.Runtime.Intrinsics.X86
open Matrix
open StateSpace
open World
open Parameters

#nowarn "9"

[<Literal>]
let InfeasibleVal = Double.NegativeInfinity


/// Takes in an attribute sequence, multiplies by the parameters and adds up the resulting 2d arrays
let addUtility (ps:Parameters) (utility:Mat) (vars:Variable seq) : Mat =
    vars 
    |> Seq.collect (fun (name, vars) -> ps.getValue name |> scale vars)
    |> addMatrices utility


///Constructs the immediate utility function needed by the value function
let addImmediateUtility ps agent world c (utility:Mat) (s:State, d:Decision) : Mat = 
    Utility.decisionVariables agent world c (s, d)
    |> addUtility ps utility 


/// Associates the appropriate Mat shape with the given state and decision
let decisionMatShape (s:State) (d:Decision) = 
    match d with
    | Travel(_, dest) -> matShape(s.Location.HasZone, dest.HasZone)
    | _ -> matShape(s.Location.HasZone, true)


//Constants for the addEvUtility function below
let private VSize = Vector256<float>.Count
let private ZeroVec = Array.create VSize 0.0 |> Vector256.Create<float>
let private OnesVec = Array.create VSize 1.0 |> Vector256.Create<float>



///Adds an interpolated approximation of the appropriate EV to the given utility matrix.
///This function is the primary bottleneck for performance, so it is written in vectorized 
///form and is highly efficient assuming the (x86) processor supports Avx and Fma instructions.
let addEvUtility (ev:float[]) (u:Mat) (ts:Mat) (destArr:int[])  =

    let mutable i = 0
    let tsSpan : ReadOnlySpan<float> = matSpan ts
    let uSpan : Span<float> = matSpan u
    let evSpan : ReadOnlySpan<float> = ReadOnlySpan(ev)
    let destSpan : ReadOnlySpan<int> = ReadOnlySpan(destArr)

    let Packed0 = Array.zeroCreate<float> VSize
    let Packed1 = Array.zeroCreate<float> VSize

    //Check if we have the necessary hardware intrinsics available,
    //otherwise fall back to the below code
    if Fma.IsSupported && Avx.IsSupported then

        let tsMaxVec = Vector128.Create<int>(DayLength) //assumes the last two columns of ev are filler (InfeasibleVal) for after the timesteps
        let L = uSpan.Length - VSize + 1
    
        //the most important loop performance-wise
        while i < L do

            //get indices into ev
            let tVec = Vector256.Create<float>(tsSpan.Slice(i))
            let tiVec0 = Avx.ConvertToVector128Int32WithTruncation(tVec)
            let tiVec1 = Vector128.Min(tiVec0, tsMaxVec)
            let dVec = Vector128.Create<int>(destSpan.Slice(i))
            let idxVec = Vector128.Add(tiVec1, dVec)

            //pack ev values vector
            for j in 0 .. VSize-1 do
                let idx = idxVec[j]
                Packed0[j] <- evSpan[idx]
                Packed1[j] <- evSpan[idx+1]

            //weight vectors
            let aVec = Vector256.Subtract(tVec, Vector256.Floor(tVec))
            let bVec = Vector256.Subtract(OnesVec, aVec)

            //lowerbound
            let packed0Vec = Vector256.Create<float>(Packed0)

            //upperbound - replace values with zero if prob is 0 to avoid NaN if value is -inf
            let packed1Vec = Vector256.Create<float>(Packed1)
            let maskVec = Avx.CompareEqual(aVec, ZeroVec)
            let packed1Vec1 = Vector256.ConditionalSelect(maskVec, ZeroVec, packed1Vec)

            //interpolate
            let uSlice = uSpan.Slice(i)
            let l1V = Fma.MultiplyAdd(bVec, packed0Vec, Vector256.Create<float>(uSlice))
            let l2V = Fma.MultiplyAdd(aVec, packed1Vec1, l1V)

            //store in the original utility array
            l2V.CopyTo(uSlice)

            i <- i+VSize
    
    //Finish elements that don't fit into the vectors and use as 
    //fallback if the hardware instruction sets are not supported
    while i < uSpan.Length do
        let ts = min tsSpan[i] (float DayLength)
        let idx = destSpan[i] + int ts
        let a = ts - floor ts
        let v = if a <= 1e-6 then evSpan[idx]
                else IFloatingPointIeee754.Lerp(evSpan[idx], evSpan[idx+1], a)
        uSpan[i] <- uSpan[i] + v
        i <- i+1


///Arrays for destination indices, used to avoid recalculating constant values on the critical path
let emptyIntArr () = Array.empty<int>
let AllDestsArr, SingleDestArr, ZerosDestArr = new ThreadLocal<_>(emptyIntArr), new ThreadLocal<_>(emptyIntArr), new ThreadLocal<_>(emptyIntArr)

///Gets a rented matrix and the appropriate destination array for use
let getDestArr (world:World) (pool:MatPool) (s:State) (decision:Decision) = 
    let ts = decisionMatShape s decision |> pool.rent
    
    //Get the appropriate location; put the correct time values in ts
    let loc = 
        match decision with
        | Travel(mode, dest) ->
            let modt = mode, s.Location, dest, s.TimeOfDay
            ignore (addMatrices ts (world.TravelTime modt))
            ignore (addMatrices ts (world.TravelWait modt))
            ignore (addMatrices ts (world.TravelAccess modt))
            Span.scaleAndShiftInPlace (matSpan ts)  TimeResolutionPerMinute s.TimeOfDay
            dest
        | _ ->
            Span.fill (matSpan ts) (s.TimeOfDay + (decisionStep s.TimeOfDay))
            s.Location

    //Get the correct destination array to pass into the addEvUtility function
    let destArr = 
        match loc with
        | Residence _ | Workplace _ -> ZerosDestArr.Value
        | NonFixed (Some d) -> 
            Array.fill SingleDestArr.Value 0 SingleDestArr.Value.Length ((world.ZIndex d)*(DayLength+2))
            SingleDestArr.Value
        | NonFixed None -> AllDestsArr.Value

    ts, destArr
    
///Finds interpolated values for the EV and adds them to the 'utility' 2d array.
let addExpectedUtility (world:World) (pool:MatPool) (s:State) (decision:Decision) (evpart:float[]) (utility:Mat) : Mat = 
    
    //Initialize destination arrays (should run only once)
    if world.NumZones <> SingleDestArr.Value.Length then
        AllDestsArr.Value <- Array.init<int> (world.NumZones*world.NumZones) (fun i -> (i%world.NumZones)*(DayLength+2))
        SingleDestArr.Value <- Array.zeroCreate<int> world.NumZones 
        ZerosDestArr.Value <- Array.zeroCreate<int> world.NumZones
    
    //get a rented 2d array for timesteps and appropriate destination array
    let ts, destArr = getDestArr world pool s decision
    
    //Do the utility interpolated pack
    addEvUtility evpart utility ts destArr
    
    //return the timestep 2d array
    pool.retn ts
    
    utility

