module CostFunction

open MKLNET

///Base interface for cost function declaration
type ICostFunction =
    
    ///Compute the cost function value of x
    abstract member Value : vector -> float

    ///Calculate the grad_f, the first derivative of the cost function with respect to x
    abstract member Gradient : vector -> vector

    ///Calculate the hessian, the second derivative of the cost function with respect to x
    abstract member Hessian : vector -> matrix
    
    ///Calculate the matrix of sum of scores for use in calculating the robust estimators
    abstract member SumOfScoreMatrix : vector * vector -> matrix

    

/// Represents the evaluation of a one-dimensional function at a point (x). Used in line search 
/// as a reduction of a multidimensional function along the search direction.
type Eval1D = 
    abstract member x   : float      //x value
    abstract member fx  : float      //Function value at x
    abstract member f'x : float      //Derivative value at x


/// Represents the evaluation of a multidimensional cost function at the point
/// x0 + (alpha * dir) where x0 is the initial vector location, dir is a search
/// direction and alpha is the distance along the search direction.
/// Internally stores the new vector and gradient with lazy evaluation. Implements
/// the Eval1D interface so it can be used in a one-dimensional line search without
/// exposing the internals.
type CostFuncEval(costFunc:ICostFunction, x0:vector, dir:vector, alpha:float) =

    //multi-dimensional function evaluation
    let x1 = lazy (x0 + (alpha * dir)).Evaluate()
    let f = lazy (costFunc.Value x1.Value)
    let g = lazy (costFunc.Gradient x1.Value)

    //one-dimensional derivative in direction dir
    let f' = lazy (g.Value.T * dir)

    //multi-dimensional
    member _.position with get() = x1.Value
    member _.value with get() = f.Value
    member _.grad with get() = g.Value

    //one-dimensional
    interface Eval1D with
        member _.x with get() = alpha
        member _.fx with get() = f.Value
        member _.f'x with get() = f'.Value