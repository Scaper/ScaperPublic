module EVCache

open System
open System.Collections.Generic
open MKLNET
open Open.Disposable
open Matrix
open StateSpace

/// An object pool which rents out arrays of the appropriate length for use in the EVCache type.
/// One should be created per thread and reused by new EVCache instances.
type EVPool (numZones:int) = 
    
    let makePool (allZones:bool) =
        let arrSize = (DayLength+2) * (if allZones then numZones else 1)
        allZones, new InterlockedArrayObjectPool<_>(
                    (fun () -> Array.zeroCreate<float> arrSize), ignore, ignore, 1000)

    let pools = Map [ makePool false; makePool true ]
    
    ///Rent an array from the pool. The parameter allZones indicates whether
    ///an thick (width numZones) array is needed.
    member _.rent (allZones:bool) = pools[allZones].Take()

    ///Return the array to the pool
    member _.retn (arr:float[]) = pools[arr.Length > (DayLength + 2)].Give(arr)

    ///How many zones were used to create this pool
    member _.nZones = numZones

    ///A zero-valued scalar Mat for one zone
    member _.zeroVarOneZone = Mat(1.0, Shape.Scalar, Array.zeroCreate<float> 1 |> ArraySegment)
    
    ///A zero-valued destination Mat for all zones
    member _.zeroVarAllZones = Mat(1.0, Shape.OneOxDs, Array.zeroCreate<float> numZones |> ArraySegment)


/// Type to store cached EV values for use in the value function. An EVCache is specific to
/// an agent and latent class, and new caches should be created for each agent/class combo.
type EVCache (pool:EVPool, defaultValue:float) = 

    ///Internal storage of the EV data
    let data = Dictionary<CacheKeyState, bool[]*float[]>()
    
    ///Internal function to create/rent arrays of appropriate length and add them to the dictionary
    ///under the appropriate state
    let newArrays (s:CacheKeyState) (loc:Location) = 
        let todo = Array.create<bool> DayLength true
        let evs = pool.rent loc.IsNonFixed
        Span.fill evs defaultValue
        data.Add(s, (todo, evs))
        todo, evs


    ///True if the EV for the given state has not been cached, i.e. if it must be calculated. 
    ///False if the EV is already cached, i.e. the appropriate row in getAllTimesteps will be filled
    ///in with a usable value. (This could be the infeasible val if the state is infeasible.)
    member _.needsCaching (s:State) = 
        let t, s0 = ifloor s.TimeOfDay, cacheKey s
        if t < DayLength then 
            let todo = match data.TryGetValue s0 with
                       | true, (td, _) -> td
                       | false, _ -> newArrays s0 s.Location |> fst
            todo[t]
        else false

    ///Gets the relevant Mat for this state, but at all timesteps. Each (integer) timestep 
    ///corresponds to one row in the Mat.
    member _.getAllTimesteps (s:State) = 
        let s0 = cacheKey s
        match data.TryGetValue s0 with
        | true, (_, evArr) -> evArr
        | false, _ -> newArrays s0 s.Location |> snd

    ///Copies the EV matrix for the given state to the cache. The provided Mat can then be 
    ///reused (e.g. returned to an object pool) without affecting the cache.
    member _.cache (s:State) ((_,_,arrIn):Mat) =
        let t, s0 = ifloor s.TimeOfDay, cacheKey s
        if t < DayLength then
            //get (or make) the appropriate arrays
            let todo, evmat =             
                match data.TryGetValue s0 with
                | true, (td, evs) -> td, evs
                | false, _ -> newArrays s0 s.Location

            //copy in the ev values
            todo[t] <- false
            match s.Location with
            | Residence _ | Workplace _ -> evmat[t] <- arrIn[0]
            | NonFixed None -> Blas.copy(pool.nZones, arrIn, 1, Span(evmat).Slice(t), DayLength+2)
            | NonFixed (Some z) -> raise(Exception("Tried to cache a single-zone other activity, which is not allowed!"))

    ///Caches an EV of zero for the given state
    member this.cacheZero (s:State) =
        this.cache s (if s.Location.IsNonFixed then pool.zeroVarAllZones else pool.zeroVarOneZone)


    //On disposal, returns rented arrays to the pool and clears the EV dictionary.
    interface IDisposable with        
        member _.Dispose() =
            for _, m in data.Values do pool.retn m
            data.Clear()