[<RequireQualifiedAccess>]
module BFGS

open System
open MKLNET
open Output
open LineSearch
open CostFunction

/// The maximum that variables should be allowed to change from iteration
/// to iteration in the starting step size for line searches
[<Literal>]
let MAX_VARIABLE_CHANGE = 2.0

/// The maximum that the previously successful line search step size is 
/// allowed to increase to begin the line search on the next iteration
[<Literal>]
let MAX_STEP_INCREASE_FACTOR = 10.0

/// The maximum number of iterations to run for
[<Literal>]
let MAXIMUM_ITERATIONS = 10000

/// Convergence requirement: change to function value from iteration to iteration is below this threshold
[<Literal>]
let STATIONARY_TOLERANCE = 1e-10

/// Convergence requirement: absolute sum of gradient is below this threshold
[<Literal>]
let GRADIENT_TOLERANCE = 1e-6


/// Calculates an appropriate step to begin a line search. Minimum of three values:
/// (a) a reasonable increase from the previous successful step size alphaOld,
/// (b) the value which would cause the variables to change by the maximum factor, and
/// (c) 1.0.
let private initialStep (alphaOld:float) (directionAbsSum:float) : float =
        
    //max possible alpha from step increase
    let stepAlpha = 
        if Double.IsFinite(alphaOld) && alphaOld > 0.0 then (alphaOld * MAX_STEP_INCREASE_FACTOR)
        else 1.0

    min (min stepAlpha MAX_VARIABLE_CHANGE/directionAbsSum) 1.0


/// Updates the provided estimate of the inverse Hessian given the change in gradient
/// and value. Changes the values of the passed in inverse Hessian and returns it.
/// Implements the expression:
/// Hnew = Hold + [(1 + g'Hg/d'g)/d'g]dd' - (dg'H + Hgd')/d'g
let private updateHessian (invH:matrix) (dgrad:vector) (dx:vector) =

    match dx.T * dgrad with
    | d_2 when d_2 > 0 ->                                               // d_2 = d'g
        let d_2i = 1.0 / d_2                                            // d_2i = 1/d'g
        let vhgamm = invH * dgrad
        let d_3 = (1.0 + (dgrad.T * vhgamm * d_2i)) * d_2i              // (1 + g'Hg/d'g)/d'g
        let mup = (((d_3 / 2.0) * dx) * dx.T - ((d_2i * vhgamm) * dx.T)).Evaluate()
        let mupT = mup.T.Evaluate()
        (invH + mup + mupT).Evaluate()
    | _ ->
        new matrix(dx.Length, dx.Length, fun r c -> if r = c then 1.0 else 0.0)
        


/// End status of the optimization algorithm
type OptimizationStatus =
    | WithinConvergenceTolerance
    | LineSearchError of LineSearchStatus
    | OptimizationError of string
    | MaximumIterations


/// Find the maximum of multidimensional function costFunc. Uses the BFGS algorithm for unconstrained 
/// optimization. Caller provides a callback intended for displaying optimization updates, which is given
/// iteration number, current function value and L2-norm of current gradient.
let maximize (costFunc:ICostFunction) (startParams:float[]) (callback:(int->float->float->unit) option) =

    let iterCallback, endCallback = 
        match callback with 
        | Some(cb) -> cb, ignore
        | None -> defaultCallbacks()
    let x = new vector(startParams.Length, startParams)

    ///Recursive function to maximize
    let rec rMaximize (x0:CostFuncEval) (invH0:matrix)  (alpha0:float) (iter:int) =
        
        //Calculate new ascent direction
        let dir = (invH0.T * x0.grad).Evaluate()
        
        //Ensure direction is finite
        let aSumDir = Vector.Asum dir
        if not(Double.IsFinite(aSumDir)) then
            OptimizationError("Non-finite direction"), x0, invH0

        else 
            //function to provide line search to evaluate cost function in direction dir with step alpha
            let fEval alpha : Eval1D = CostFuncEval(costFunc, x0.position, dir, alpha)
            
            //initial step
            let alpha = initialStep alpha0 aSumDir

            //Conduct the line search to find the new point
            let status, eval = lineSearch fEval alpha
            
            //Convert Eval1D returned by linesearch to CostFuncEval. Won't fail since it is created by fEval.
            let x1 = eval :?> CostFuncEval
            let absSumGrad = Vector.Asum(x1.grad)

            //Callback to print
            iterCallback iter x1.value (sqrt (x1.grad.T * x1.grad))
                
            // *** Potential stopping conditions: ***
            if status <> LineSearchStatus.AcceptablePointFound then
                LineSearchError(status), x1, invH0

            else if not(Double.IsFinite absSumGrad) then
                OptimizationError("Non-finite gradient"), x1, invH0

            else if abs (x1.value-x0.value) <= STATIONARY_TOLERANCE && absSumGrad < GRADIENT_TOLERANCE then
                WithinConvergenceTolerance, x1, invH0

            else if iter >= MAXIMUM_ITERATIONS then
                MaximumIterations, x1, invH0

            //No stopping condition found: do a recursive iteration from new point with updated inverse Hessian
            else
                let invH = updateHessian invH0 ((x0.grad - x1.grad).Evaluate()) ((eval.x*dir).Evaluate())
                rMaximize x1 invH eval.x (iter+1)
    
    //starting point cost function evaluation
    let x0 = CostFuncEval(costFunc, x, new vector(x.Length), 0.0)
    
    //starting inverse Hessian matrix
    let invHessian0 = 
        try (costFunc.SumOfScoreMatrix(x0.position, x0.grad) |> Matrix.Inverse).Evaluate()
        with
        | _ -> new matrix(x0.position.Length, x0.position.Length, fun r c -> if r = c then 1.0 else 0.0)

    //callback for iteration 0
    iterCallback 0 x0.value (sqrt (x0.grad.T * x0.grad))

    //run recursive maximization function
    let status, eval, invH = rMaximize x0 invHessian0 1.0 1

    endCallback status

    status, eval, invH
    


