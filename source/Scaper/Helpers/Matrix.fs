module Matrix

open System
open Open.Disposable
open MKLNET

///Matrix shape 
type Shape =
| Scalar    //Single value
| OneDxOs   //Column: one destination (col) by many origins (rows)
| OneOxDs   //Row: one origin (row) by many destinations (cols)
| ODMat     //Square: many origins (rows) by destinations (cols)

///Represents a matrix of float values with a certain shape and multiplicative scale
type Mat = float * Shape * ArraySegment<float>

///Gets the underlying data of the matrix represented by a float Span
let matSpan ((_,_,m):Mat) : Span<float> = m

///Creates a scalar Var with the given value for use in utility
let inline scalar v : Mat seq = seq {(1.0, Shape.Scalar, ArraySegment([| v |]))}

///Gets the appropriate shape for the combo of origin/destination existence
let matShape (hasOrig:bool, hasDest:bool) = 
    match hasOrig, hasDest with
    | true, true -> Shape.Scalar
    | true, false -> Shape.OneOxDs
    | false, true -> Shape.OneDxOs
    | false, false -> Shape.ODMat

let matSize (nZones:int) (hasOrig:bool, hasDest:bool) = 
    match hasOrig, hasDest with
    | true, true -> 1
    | true, false -> nZones
    | false, true -> nZones
    | false, false -> nZones*nZones


///A pool for keeping track of Mats so we don't have to create and destroy
///their underlying arrays
type MatPool (numZones:int) = 
    
    let makePool (shape:Shape) (size:int) =
        shape, new InterlockedArrayObjectPool<Mat>(
                (fun () -> Mat(1.0, shape, ArraySegment(Array.zeroCreate<float> size))), 
                (fun (_, _, arr) -> Span.fill arr 0.0), 
                (fun _ -> ()), 100)

    let pools = Map [
        makePool Shape.Scalar 1 
        makePool Shape.OneOxDs numZones
        makePool Shape.OneDxOs numZones
        makePool Shape.ODMat (numZones*numZones)
    ]

    member _.nZones = numZones

    member _.rent(t:Shape) = pools[t].Take()

    member _.retn((s, sh, arr):Mat) = pools[sh].Give((s, sh, arr))

    member x.retn(vs:Mat seq) = vs |> Seq.iter x.retn




///Indicates that a matrix is not square, vector or scalar
exception MatrixShapeException

///Indicates a mismatch between the sizes of two matrices when trying to broadcast from one to the other.
exception MatrixSizeMismatchException

let private rowColCounts (shape:Shape) (arr:ArraySegment<float>) : int*int =
    match shape with
    | Scalar  -> if arr.Count <> 1 then raise MatrixShapeException
                 1,1
    | OneDxOs -> if arr.Count = 1 then raise MatrixShapeException
                 arr.Count, 1
    | OneOxDs -> if arr.Count = 1 then raise MatrixShapeException
                 1, arr.Count
    | ODMat   -> let n = int (sqrt (float arr.Count))
                 if arr.Count <> n*n then raise MatrixShapeException
                 n, n


/// Divides num by denom, broadcasting the sizes appropriately.
///Leaves matrix scales unchanged
let divideIgnoreZeroDenoms ((_, numShape, numArray):Mat) ((_, denomShape, denomArray):Mat) =

    let nRows, nCols = rowColCounts numShape numArray 
    let dRows, dCols = rowColCounts denomShape denomArray

    if nRows <> dRows || dCols <> 1 then raise MatrixSizeMismatchException
        
    let numSpan = Span.readWrite numArray
    let denomSpan = Span.readOnly denomArray

    match nCols with 
    | 1 -> 
        for r in 0 .. nRows-1 do
            if denomSpan[r] > 0.0 then 
                numSpan[r] <- numSpan[r]/denomSpan[r]
    | _ -> 
        for r in 0 .. nRows-1 do
            if denomSpan[r] > 0.0 then
                Blas.scal(nCols, 1.0/denomSpan[r], numSpan.Slice(r*nCols), 1)


///Scales the matrices by their float partners, then adds to 'accum' in a fast 
///and efficient way without allocating any new arrays or changing any array values in 'mats'.
let addMatrices ((accumScale, accumShape, accumArr):Mat) (mats:Mat seq) : Mat =

    let mutable s = 0.0  //for scalars: add all at once
    let accumSpan = Span.readWrite accumArr

    let nRows, nCols = rowColCounts accumShape accumArr
    
    if accumScale <> 1.0 then Span.scaleInPlace accumSpan accumScale

    for scale, shape, arr in mats do
        
        let mRows, mCols = rowColCounts shape arr
        let mSpan = Span.readOnly arr

        if (mRows <> 1 && nRows <> 1 && mRows <> nRows) || (mCols <> 1 && nCols <> 1 && mCols <> nCols) then 
            raise MatrixSizeMismatchException
 
        match accumShape, shape with
        | _, Shape.Scalar -> 
            s <- s + scale*mSpan[0]
        | Shape.Scalar, _ ->
            s <- s + scale*(Span.sum mSpan)
        | Shape.ODMat, Shape.ODMat | Shape.OneOxDs, Shape.OneOxDs | Shape.OneDxOs, Shape.OneDxOs ->
            Blas.axpby(mSpan.Length, scale, mSpan, 1, 1.0, accumSpan, 1)
        | Shape.ODMat, Shape.OneDxOs ->
            for c in 0 .. (nCols-1) do
                Blas.axpby(mRows, scale, mSpan, 1, 1.0, accumSpan.Slice(c), nCols)
        | Shape.OneDxOs, Shape.ODMat ->
            for c in 0 .. (mCols-1) do
                Blas.axpby(mRows, scale, mSpan.Slice(c), mCols, 1.0, accumSpan, 1)
        | Shape.ODMat, Shape.OneOxDs ->
            for r in 0 .. (nRows-1) do
                Blas.axpby(mCols, scale, mSpan, 1, 1.0, accumSpan.Slice(r*nCols), 1)
        | Shape.OneOxDs, Shape.ODMat ->
            for r in 0 .. (mRows-1) do 
                Blas.axpby(mCols, scale, mSpan.Slice(r*mCols), 1, 1.0, accumSpan, 1)
        | _ -> raise MatrixSizeMismatchException

    //add accumulated scalar
    if s <> 0 then Span.shiftInPlace accumSpan s

    (1.0, accumShape, accumArr)

///Scales all the Mats in the input sequence. Does not impact the underlying arrays, but instead multiplies the 'scale' part.
let scale (mats:Mat seq) (scalar) : Mat seq = 
    Seq.map (fun (sc, sh, arr) -> (scalar*sc, sh, arr)) mats
    

///Takes the natural log of the matrix
let logInPlace ((s, sh, arr):Mat) = 
    MKLNET.Vml.Ln(arr, arr)
    (s, sh, arr)

///Exponentiates the matrix
let expInPlace ((s, sh, arr):Mat) = 
    MKLNET.Vml.Exp(arr, arr)
    (s, sh, arr)