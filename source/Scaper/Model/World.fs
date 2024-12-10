module World

open System
open MKLNET
open Open.Disposable
open Matrix


type WorldPool(nZones:int) = 
    inherit InterlockedArrayObjectPool<float[]>(
        (fun () -> Array.zeroCreate<float> (nZones*nZones)),
        (fun a -> Span.fill a 0.0), (fun _ -> ()), 1000)


///Given a (possibly sampled) array of land use variables and a zone index,
///builds a var sequence for use in the utility function
let landUseVariable (luArr:float[]) (zIdx:int option) : Mat seq = 
    let s, arr = match zIdx with
                 | Some i -> Shape.Scalar, ArraySegment(luArr, i, 1)
                 | None -> Shape.OneDxOs, ArraySegment(luArr)
    seq { (1.0, s, arr) }

///Represents information about the world available to an agent in the utility function.
///This is used instead of directly referencing LandUse and Network to allow for zone sampling 
///(which is done in the ZoneSampling module and we generally don't need to think about).
type World internal (zones:int[], pool:WorldPool option, corrections:float[] option, overrideThreading:bool) =

    let mutable isDisposed = false

    let nZones = zones.Length
    let isSampled = zones.Length < LandUse.N

    //function to sample network OD matrices
    let sampleNetwork (_) (m:float[]) = 
        let sampled = pool.Value.Take()
        for r in 0 .. (nZones-1) do
            Vml.PackV(nZones, ReadOnlySpan(m, zones[r]*LandUse.N, LandUse.N), zones, Span(sampled, r*nZones, nZones))
        sampled

    //if the world is sampled then create sampled networks, otherwise use the thread-local versions
    let ttMap = 
        if isSampled then 
            lock Network.TTMap0 (fun () -> Map.map sampleNetwork Network.TTMap0)
        else if overrideThreading then Network.TTMap0 else Network.TTMap.Value
    let twaitMap = 
        if isSampled then
            lock Network.TWaitMap0 (fun () -> Map.map sampleNetwork Network.TWaitMap0)
        else if overrideThreading then Network.TWaitMap0 else Network.TWaitMap.Value
    let taccessMap = 
        if isSampled then 
            lock Network.TAccessMap0 (fun () -> Map.map sampleNetwork Network.TAccessMap0)
        else if overrideThreading then Network.TAccessMap0 else Network.TAccessMap.Value
    let tcMap =
        if isSampled then 
            lock Network.TCMap0 (fun () -> Map.map sampleNetwork Network.TCMap0)
        else if overrideThreading then Network.TCMap0 else Network.TCMap.Value

    //function to sample land use
    let sampleLandUse (m:float[]) =
        let sampled = Array.zeroCreate nZones
        Vml.PackV(zones.Length, m, zones, sampled)
        sampled

    //if the world is sampled then create sampled land use variables, otherwise use the thread-local versions
    let logPop =
        if isSampled then 
            lock LandUse.aLogPop0 (fun () -> sampleLandUse LandUse.aLogPop0)
        else if overrideThreading then LandUse.aLogPop0 else LandUse.aLogPop.Value
    let logEmp =
        if isSampled then 
            lock LandUse.aLogEmp0 (fun () -> sampleLandUse LandUse.aLogEmp0)
        else if overrideThreading then LandUse.aLogEmp0 else LandUse.aLogEmp.Value
    let parkingRate =
        if isSampled then 
            lock LandUse.aPRate0 (fun () -> sampleLandUse LandUse.aPRate0)
        else if overrideThreading then LandUse.aPRate0 else LandUse.aPRate.Value

    //zone indexing
    let zIndex, zIndexValue =
        if isSampled then
            let zIndices = Array.indexed zones |> Array.map (fun (x,y)->(y,x)) |> dict
            let zIndV = fun zone -> zIndices[zone] 
            let zInd = fun zone -> match zone with
                                   | Some z -> Some(zIndices[z])
                                   | None -> None
            zInd, zIndV
        else id, id

    //travel timestep caching to avoid recalculating commonly-used timestep jumps
    let travelTimesteps (mode, orig:Location, dest:Location) = Network.travelTimesteps nZones ttMap twaitMap taccessMap (mode, zIndex orig.Zone, zIndex dest.Zone)
    let ttsCacher = Cacher()

    //transposed corrections
    let correctionsT = 
        if corrections.IsSome then 
            let t = pool.Value.Take() 
            Some(transpose corrections.Value nZones nZones t) 
        else None

    ///Is this a sampled world?
    member _.IsSampled = isSampled

    ///Get the zone index in this World of the (full world) zone
    member x.ZIndex loc = zIndexValue loc

    ///The list of zones this world contains
    member _.Zones = zones

    ///The number of zones in this (possibly sampled) world
    member _.NumZones = nZones


    ///Allows access to the internal pool for extending classes
    member internal _.WorldPool = pool

    ///Allows access to the corrections array for extending classes
    member internal _.CorrArray = corrections

    ///A function to get corrections for use in the utility function for sampled worlds
    member _.Corrections (orig:Location, dest:Location) : Mat seq = 
        match corrections with
        | Some c ->
            let arr = match zIndex orig.Zone, zIndex dest.Zone with
                      | Some o, Some d -> ArraySegment(c, nZones*o + d, 1)
                      | Some o, None -> ArraySegment(c, nZones*o, nZones)
                      | None, Some d -> ArraySegment(correctionsT.Value, nZones*d, nZones)
                      | None, None -> ArraySegment(c)
            seq { (1.0, matShape(orig.HasZone, dest.HasZone), arr) }
        | None -> scalar 0.0

    ///The travel time by the given mode between origin and destination at the given time of day. An input of None represents all origins or destinations.
    abstract member TravelTime : Mode * Location * Location * float -> Mat seq
    default _.TravelTime (mode, orig, dest, time) = Network.levelOfService nZones ttMap (mode, zIndex orig.Zone, zIndex dest.Zone, time)

    ///The travel wait time for the given mode between origin and destination. An input of None represents all origins or destinations.
    member _.TravelWait (mode, orig:Location, dest:Location, time) = Network.levelOfService nZones twaitMap (mode, zIndex orig.Zone, zIndex dest.Zone, time)

    ///The travel wait time for the given mode between origin and destination. An input of None represents all origins or destinations.
    member _.TravelAccess (mode, orig:Location, dest:Location, time) = Network.levelOfService nZones taccessMap (mode, zIndex orig.Zone, zIndex dest.Zone, time)

    ///The travel cost for the given mode between origin and destination. An input of None represents all origins or destinations.
    abstract member TravelCost : Mode * Location * Location * float -> Mat seq
    default _.TravelCost (mode, orig, dest, time) = Network.levelOfService nZones tcMap (mode, zIndex orig.Zone, zIndex dest.Zone, time)

    ///The range of integral timesteps into the future that are necessary to calculate for travel by the given mode from origin to destination.
    member _.TravelTimesteps = travelTimesteps |> ttsCacher.memoize

    ///Gets the log population for the zone at the given index, or for all zones if zindex is None.
    member _.LogPop (loc:Location) = landUseVariable logPop (zIndex loc.Zone)

    ///Gets the log employment for the zone at the given index, or for all zones if zindex is None.
    member _.LogEmp (loc:Location) = landUseVariable logEmp (zIndex loc.Zone)

    ///Gets the parking rate for the zone at the given index, or for all zones if zindex is None.
    member _.ParkingRate (loc:Location) = landUseVariable parkingRate (zIndex loc.Zone)

    ///Public constructor
    new(zones:int[], pool:WorldPool option, corrections:float[] option) = new World(zones, pool, corrections, false)

    //World implements Dispose to it can be called with the "use" keyword which helps sampled zones clean up after themselves
    interface IDisposable with 
        member this.Dispose() = 
            if not isDisposed then
                isDisposed <- true
                if this.IsSampled then
                    ttMap.Values |> Seq.iter pool.Value.Give
                    tcMap.Values |> Seq.iter pool.Value.Give
                    if corrections.IsSome then 
                        pool.Value.Give corrections.Value
                        pool.Value.Give correctionsT.Value


let fullWorld() = new World([| 0 .. LandUse.N-1 |], None, None, false)

let baseWorld() = new World([| 0 .. LandUse.N-1 |], None, None, true)