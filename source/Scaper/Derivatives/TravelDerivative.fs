module TravelDerivative

open System
open Matrix
open World
open UtilityFuncs
open StateSpace

    
[<Literal>]
let Epsilon = 1e-5

///Type representing dTT/dx, the derivative of travel time with respect to some
///scalar change x.
type TTDerivative = Mode * Location * Location -> float[]


///Make the function which represents a partial travel time derivative dTT/dx
let makeDeriv (world:World) (mode:Mode) (orig:Location) (dest:Location) : TTDerivative =
    let c = Cacher()
    (fun (m:Mode, o:Location, d:Location) -> 
        let arr = Array.zeroCreate (matSize world.NumZones (o.HasZone, d.HasZone))
        if m = mode && o = orig && d = dest then
            Array.Fill(arr, 1.0)
        arr)
    |> c.memoize


///Gets the numerical derivative of the given reward function across the change in worlds 
///(between world0 and world1) and start states (between s0 and s1).
let numericalRewardDerivative (pool:MatPool) (addReward:World->State->Decision->Mat->Mat) (world0:World) (world1:World) (s0:State) (s1:State) (d:Decision) = 
    let r = decisionMatShape s0 d |> pool.rent |> addReward world0 s0 d     //utility without epsilon change
    let rSpan = matSpan r
    Span.scaleInPlace rSpan -1.0                                    //negative
    addReward world1 s1 d r |> ignore                               //add utility with epsilon change
    Span.scaleInPlace rSpan (1.0/Epsilon)                           //divide by epsilon
    r
    

let copyArray (pool:WorldPool option) (array:float[] option) : float[] option = 
    if array.IsSome && pool.IsSome then
        let newArr = pool.Value.Take()
        MKLNET.Blas.copy(array.Value, newArr)
        Some(newArr)
    else None


///Creates a world in which there is an epsilon-scaled travel time change in the direction of the given derivative.
type EpsilonTTWorld(baseW:World, deriv:TTDerivative) =
    inherit World(baseW.Zones, baseW.WorldPool, (copyArray baseW.WorldPool baseW.CorrArray))

    override _.TravelTime (mode, orig, dest, time) = 
        let btt = base.TravelTime (mode, orig, dest, time)
        seq {
            yield! btt
            yield (Epsilon, matShape(orig.HasZone, dest.HasZone), ArraySegment(deriv(mode, orig, dest)))
        }

///Creates a world in which there is an epsilon-scaled travel cost change in the direction of the given derivative.
type EpsilonTCWorld(baseW:World, deriv:TTDerivative) =
    inherit World(baseW.Zones, baseW.WorldPool, (copyArray baseW.WorldPool baseW.CorrArray))

    override _.TravelCost (mode, orig, dest, time) = 
        let btc = base.TravelCost (mode, orig, dest, time)
        let eps = (Epsilon, matShape(orig.HasZone, dest.HasZone), ArraySegment(deriv(mode, orig, dest)))
        seq {
            yield! btc
            yield eps
        }