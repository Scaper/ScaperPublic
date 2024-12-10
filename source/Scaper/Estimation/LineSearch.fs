module LineSearch

open System
open CostFunction

/// Describes the result of a line search operation
type LineSearchStatus = 
    | AcceptablePointFound = 0
    | MaxIterationsReached = 1
    | FiniteStepNotFound = 2

/// The ratio between successive derivatives to accept a point as an acceptable improvement for the line search
[<Literal>]
let ACCEPTABLE_DERIVATIVE_RATIO = 0.95

/// Smallest bracket size to continue searching - any smaller will be returned as acceptable result
[<Literal>]
let MINIMUM_BRACKET_SIZE = 1e-16

/// Factor to expand the bracket size when candidate not found in current bracket
[<Literal>]
let BRACKET_EXPANSION_FACTOR = 10.0 

/// The maximum step size to allow in line search
[<Literal>]
let MAX_STEP = 1e10

/// The maximum number of iterations to allow in line search bracketing phase
[<Literal>]
let MAX_SEARCH_ITERATIONS = 200

/// Uses cubic interpolation to pick the best potential point in interval [end0, end1] given function
/// evaluations and derivatives at two points (lower and upper).
let private pickAlphaInInterval (end0:float) (end1:float) (lower:Eval1D, upper:Eval1D) = 
    // Find interpolating Hermite polynomial in the z-space, where z = alpha1 + (alpha2 - alpha1)*z
    // Determine the coefficients of the cubic polynomial that interpolates f and f' at alpha1 and alpha2.
    let deltaAlpha = upper.x - lower.x
    let coeff = [|                                                                  //Coefficients:
        (lower.f'x + upper.f'x) * deltaAlpha - 2.0 * (upper.fx - lower.fx)          //z^3
        3.0 * (upper.fx - lower.fx) - (2.0 * lower.f'x + upper.f'x) * deltaAlpha    //z^2
        deltaAlpha * lower.f'x                                                      //z^1
        upper.fx                                                                    //z^0
    |]
    
    // Convert bounds to the z-space. Make sure zlb <= zub so that [zlb,zub] is an interval
    let zlb, zub =
        match (end0 - lower.x) / deltaAlpha, (end1 - lower.x) / deltaAlpha with
        | a, b when a <= b -> a, b
        | a, b -> b, a

    // Minimize polynomial over interval [zlb, zub]
    //Stationary points of cubic = roots of quadratic derivative
    let stationaryPoints = 
        let a, b, c = (3.0 * coeff[0]), (2.0 * coeff[1]), (coeff[2])
        if a = 0 then [ -c/b ]                          //linear root (cubic is quadratic!)
        else
            let D = b**2 - (4.0*a*c) //discriminant
            if D > 0 then
                let d = sqrt D
                [(-b + d)/(2.0*a); (-b - d)/(2.0*a)]    //quadratic roots
            else []                                     //no real roots

    /// Find the minimizer of the cubic polynomial defined by coefficients
    /// in the interval lowerBound <= alpha <= upperBound
    let z = stationaryPoints 
            |> List.filter (fun x -> x > zlb && x < zub) 
            |> List.append [zlb; zub] 
            |> List.maxBy (fun x -> coeff[0] * (x ** 3.0) + coeff[1] * (x ** 2.0) + coeff[2] * x + coeff[3])

    lower.x + (z * deltaAlpha)



/// Performs a bracketing-sectioning linesearch for a high value of a one-dimensional function f 
/// which takes positive input x. The algorithm first searches the bracket [0, xtest], and if 
/// this does not yield a candidate point the algorithm will recursively double xtest to see if 
/// an acceptable high point can be found further from x=0.
let lineSearch (f:float->Eval1D) (xtest:float) : LineSearchStatus * Eval1D =

    // Starting function evaluation
    let evStart = f 0.0
    
    /// Finds a feasible step length by halving alpha until it finds 
    /// a value for which the cost function is finite
    let rec feasibleStep (x:float) (moreTries:int) =
        let ev1 = f x
        if Double.IsFinite(ev1.fx) then Some(ev1)
        else if moreTries <= 0 then None
        else feasibleStep (x/2.0) (moreTries-1)

    /// Recursively searches for a bracket until an acceptable one is found or until tries run out.
    /// Assumes everything below ev0 is already ruled out.
    let rec bracket (f0:Eval1D, f1:Eval1D) (moreTries:int) =
        
        // Does the proposed function point reduce the gradient enough from the start gradient to return as an acceptable point?
        if abs f1.f'x <= ACCEPTABLE_DERIVATIVE_RATIO * evStart.f'x then
            LineSearchStatus.AcceptablePointFound, f1

        // Is the proposed function point too close to the bracket lowerbound? This can indicate roundoff error or closing in on an acceptable point.
        else if abs((f1.x - f0.x) * f1.f'x) < MINIMUM_BRACKET_SIZE then
            LineSearchStatus.AcceptablePointFound, f1

        // Have we reached the maximum number of iterations?
        else if moreTries <= 0 then 
            LineSearchStatus.MaxIterationsReached, f1

        // No stopping condition found: narrow or widen bracket and try again
        
        // Narrow bracket if function is lower at upperbound than lowerbound or has negative derivative 
        // at upperbound, indicating the best candidate should be found inside the bracket.
        else if f1.fx <= f0.fx || f1.f'x < 0 then
            
            //Find candidate endpoint for narrower bracket within middle 60% of current bracket
            let evCand = pickAlphaInInterval (0.8*f0.x + 0.2*f1.x) (0.2*f0.x + 0.8*f1.x) (f0, f1) 
                         |> f

            //If function is lower or derivative is negative at candidate then look below it, otherwise above
            if evCand.fx <= f0.fx || evCand.f'x < 0 then 
                bracket (f0, evCand) (moreTries-1)
            else
                bracket (evCand, f1) (moreTries-1)
        
        // Widen bracket if function is higher at upperbound than lowerbound and continues to increase,
        // indicating the best candidate is above the bracket.
        else 
            let upper = 
                let lb = (2.0*f1.x) - f0.x
                if lb >= MAX_STEP then 
                    MAX_STEP
                else
                    let ub = min MAX_STEP (f1.x + BRACKET_EXPANSION_FACTOR*(f1.x-f0.x))
                    pickAlphaInInterval lb ub (f0, f1)
                
            bracket (f1, f upper) (moreTries-1)


    //Find a feasible step and do the bracketing search
    match feasibleStep xtest 20 with
    | Some(evTest0) -> bracket (evStart, evTest0) MAX_SEARCH_ITERATIONS
    | None -> LineSearchStatus.FiniteStepNotFound, evStart