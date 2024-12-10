module Output

open System
open System.Diagnostics
open MKLNET
open Parameters
open CostFunction

///Produce a callback function to run at each iteration of an optimization 
///and one to run after optimization has finished.
let defaultCallbacks() = 

    let mutable iteration, fmax, gradnorm, elapsed = 0, Double.MinValue, Double.MaxValue, System.TimeSpan(0)
    let printProgress() = printfn $"   {iteration,6}    |   {(int elapsed.TotalHours),3}{elapsed:``\:mm\:ss``}    |     {fmax,13:g9}     |    {gradnorm,12:g7}"
    let timer = Stopwatch()
    
    let callback (iter:int) (fx:float) (gnorm:float) =
        if fx > fmax*0.95 || gnorm < gradnorm*0.6 || timer.Elapsed.TotalSeconds > elapsed.TotalSeconds * 1.15 + 1.0 then
            iteration <- iter
            fmax <- max fx fmax
            gradnorm <- min gnorm gradnorm
            elapsed <- timer.Elapsed
            printProgress()

    let endCallback (status) =
        timer.Stop()
        elapsed <- timer.Elapsed
        printfn $"Optimizer stopped with status: {status}"
        printfn "\nFinal iteration:"
        printProgress()

    printfn $"\n  Iteration  |  Elapsed Time  |  Best Log Likelihood  |  Best Grad L2 Norm"   

    timer.Start()

    callback, endCallback


///Print the output from estimation
let printEstimationResult (ps:Parameters) (costFunc:ICostFunction) (opt:CostFuncEval) (invHEst:matrix option) =
    
    printfn "%s" ("\nCalculating robust standard errors using " + 
        if invHEst.IsSome
            then "estimated inverse Hessian from BFGS. (Turn on numerical Hessian with -H)" 
            else "numerical Hessian at optimum point. Takes a while if estimating many variables.")
    
    //inverse Hessian matrix
    let Hinv = match invHEst with
               | Some m -> m
               | None -> (costFunc.Hessian opt.position |> Matrix.Inverse).Evaluate()    
    
    //calculate standard errors
    let B = costFunc.SumOfScoreMatrix(opt.position, opt.grad)
    let sandwich = (Hinv * B * Hinv).Evaluate()
    let se = Array.init (opt.position.Length) (fun i -> sqrt sandwich[i, i])
    
    //print the output estimates 
    printfn $"\nVariable                                            Estimate     Std Error"
    Array.zip3 ps.estParams opt.position.Array[0..opt.position.Length-1] se
    |> Seq.iter (fun (name, v, e) -> printfn $"{name,-50}{v,10:f5}{e,13:f5}")


