[<RequireQualifiedAccess>]
module Span

open System
open System.Numerics

//Vectorized extensions to work with spans of floats

///Get a read-write span from an array segment
let inline readWrite (a:ArraySegment<'T>) = Span(a.Array, a.Offset, a.Count)

///Get a read-only span from an array segment
let inline readOnly (a:ArraySegment<'T>) = ReadOnlySpan(a.Array, a.Offset, a.Count) 

///Float vector size for working with vectorized operations
let private VSize = Vector<float>.Count

///Fills all elements of the span with the given value
let fill (span:Span<float>) (value:float) =
    let L = span.Length - VSize + 1
    let mutable i = 0
    let valV = Vector<float>(value)
    while i < L do
        valV.CopyTo(span.Slice(i))
        i <- i + VSize
    while i < span.Length do
        span[i] <- value
        i <- i + 1
        

///Adds 'shift' to all elements in the span
let shiftInPlace (span:Span<float>) (shift:float) =
    let L = span.Length - VSize + 1
    let mutable i = 0
    let shiftV = Vector<float>(shift)
    while i < L do
        let slice = span.Slice(i)
        Vector.Add(Vector<float>(slice), shiftV).CopyTo(slice)
        i <- i + VSize
    while i < span.Length do
        span[i] <- span[i] + shift
        i <- i + 1
        

///Multiplies all element of the span by 'scale'
let scaleInPlace (span:Span<float>) (scale:float) = 
    let L = span.Length - VSize + 1
    let mutable i = 0
    while i < L do
        let slice = span.Slice(i)
        Vector.Multiply(Vector<float>(slice), scale).CopyTo(slice)
        i <- i + VSize
    while i < span.Length do
        span[i] <- span[i]*scale
        i <- i + 1


///Multiplies all element of the span by 'scale'
let scaleAndShiftInPlace (span:Span<float>) (scale:float) (shift:float) = 
    let L = span.Length - VSize + 1
    let mutable i = 0
    let shiftV = Vector<float>(shift)
    while i < L do
        let slice = span.Slice(i)
        let scaled = Vector.Multiply(Vector<float>(slice), scale)
        Vector.Add(scaled, shiftV).CopyTo(slice)
        i <- i + VSize
    while i < span.Length do
        span[i] <- span[i]*scale + shift
        i <- i + 1


///Returns the sum of all elements of the span
let sum (span:ReadOnlySpan<float>) =
    let L = span.Length - VSize + 1
    let mutable i, vs = 0, Vector<float>.Zero
    while i < L do 
        vs <- vs + Vector<float>(span.Slice(i))
        i <- i + VSize
    let mutable s = Vector.Sum(vs)
    while i < span.Length do 
        s <- s + span[i]
        i <- i + 1
    s


///Returns the minimum value of the element-wise sum of the three spans
let minSum (span1:ReadOnlySpan<float>) (span2:ReadOnlySpan<float>) (span3:ReadOnlySpan<float>) = 
    let L = span1.Length - VSize + 1
    let mutable i, vs, s = 0, Vector<float>(Double.PositiveInfinity), Double.PositiveInfinity
    while i < L do 
        let v1 = Vector.Add(Vector<float>(span1.Slice(i)), Vector<float>(span2.Slice(i)))
        let v2 = Vector.Add(v1, Vector<float>(span3.Slice(i)))
        vs <- Vector.Min(vs, v2)
        i <- i + VSize
    while i < span1.Length do 
        s <- min s (span1[i] + span2[i] + span3[i])
        i <- i + 1
    i <- 0
    while i < VSize do
        s <- min s vs[i]
        i <- i + 1
    s


///Returns the maximum value of the element-wise sum of the three spans
let maxSum (span1:ReadOnlySpan<float>) (span2:ReadOnlySpan<float>) (span3:ReadOnlySpan<float>) = 
    let L = span1.Length - VSize + 1
    let mutable i, vs, s = 0, Vector<float>(Double.NegativeInfinity), Double.NegativeInfinity
    while i < L do 
        let v1 = Vector.Add(Vector<float>(span1.Slice(i)), Vector<float>(span2.Slice(i)))
        let v2 = Vector.Add(v1, Vector<float>(span3.Slice(i)))
        vs <- Vector.Max(vs, v2)
        i <- i + VSize
    while i < span1.Length do 
        s <- max s (span1[i] + span2[i] + span3[i])
        i <- i + 1
    i <- 0
    while i < VSize do
        s <- max s vs[i]
        i <- i + 1
    s


///Returns the minimum value in the span
let min (span:ReadOnlySpan<float>) = 
    let L = span.Length - VSize + 1
    let mutable i, vs, s = 0, Vector<float>(Double.PositiveInfinity), Double.PositiveInfinity
    while i < L do 
        vs <- Vector.Min(vs, Vector<float>(span.Slice(i)))
        i <- i + VSize
    while i < span.Length do 
        s <- min s span[i]
        i <- i + 1
    i <- 0
    while i < VSize do
        s <- min s vs[i]
        i <- i + 1
    s

///Returns the maximum value in the span
let max (span:ReadOnlySpan<float>) = 
    let L = span.Length - VSize + 1
    let mutable i, vs, s = 0, Vector<float>(Double.NegativeInfinity), Double.NegativeInfinity
    while i < L do 
        vs <- Vector.Max(vs, Vector<float>(span.Slice(i)))
        i <- i + VSize
    while i < span.Length do 
        s <- max s span[i]
        i <- i + 1
    i <- 0
    while i < VSize do
        s <- max s vs[i]
        i <- i + 1
    s

///Normalizes the span in place, making it sum to 1
let normalizeInPlace (span:Span<float>) =
    let s = sum span
    let L = span.Length - VSize + 1
    let mutable i = 0
    while i < L do
        let slice = span.Slice(i)
        Vector.Divide(Vector<float>(slice), s).CopyTo(slice)
        i <- i + VSize
    while i < span.Length do
        span[i] <- span[i]/s
        i <- i + 1


    
///Returns a random index from a probability distribution
let chooseIndex (dist: ReadOnlySpan<float>) : int =
    let s = sum dist
    let r = Random.Shared.NextDouble()
    let target = r * s
    let mutable i = 0
    let mutable curr = dist[0]
    while curr < target && i < (dist.Length - 1) do 
        i <- i+1
        curr <- curr + dist[i]
    i


