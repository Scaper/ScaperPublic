module LogLikelihoodLC

open System
open FSharp.Collections.ParallelSeq
open MKLNET
open MKLNET.Expression
open Parameters
open CostFunction

///Sum of vector sequence. Assumes sequence as at least one member, will throw exception if not!
let inline vecSum (vecs:VectorExpression seq) = Seq.fold (fun (l:VectorExpression) (r:VectorExpression) -> (l+r).Evaluate()) (Seq.head vecs) (Seq.tail vecs)

///Sum of matrix sequence. Assumes sequence as at least one member, will throw exception if not!
let inline matSum (mats:MatrixExpression seq) = Seq.fold (fun (l:MatrixExpression) (r:MatrixExpression) -> (l+r).Evaluate()) (Seq.head mats) (Seq.tail mats)


/// Type representing an observation with choiceset which has been formatted to be used in a latent class
/// log likelihood cost function. 
type LCFormattedObservation = {
    ///The class data, in an array with indices corresponding to latent classes.
    ///Each class has a vector of estimation data by parameter and float of fixed utility.
    ClassData : (vector*float)[]

    ///The choice data, in an array with indices corresponsing to latent classes.
    ///Each class has a matrix of estimation data by parameter and choiceset alternative, 
    ///and a vector of fixed utility by choiceset alternative.
    ChoiceData : (matrix*vector)[]

    ///The weight of this observation among the overall observations. Multiplied by the
    ///log likelihood of the observation in the sum of log likelihoods.
    Weight : float
}

///A cost function class to be used in estimation. Provides the log likelihood, gradient and
///sum of scores matrix for a latent class model.
type LatentClassLogitLL (parallelism:int, nClasses:int, data:LCFormattedObservation[]) =

    ///The probabilities of each class for the given observation index
    let classProbabilities (paramVec:vector) (obsIdx:int) =
        data[obsIdx].ClassData
        |> Array.map (fun (vars, fixedU) -> exp (vars.T * paramVec + fixedU))
        |> normalizeSumTo1

    ///The probabilites of each daypath for the given observation index
    let conditionalProbabilities (paramVec:vector) (obsIdx:int) =
        let cp (vars:matrix, fixedU:vector) =
            let eU = Vector.Exp(vars*paramVec + fixedU).Evaluate()
            let s = Vector.Asum(eU)
            (eU / s).Evaluate()
        data[obsIdx].ChoiceData |> Array.map cp
    
    ///The log likelihood of the given observation index
    let obsLogLikelihood (paramVec:vector) (obsIdx:int) =
        let clP = classProbabilities paramVec obsIdx
        let chP = conditionalProbabilities paramVec obsIdx
        let P = (Array.map2 (*) chP clP |> vecSum).Evaluate()
        log(P[0])

    ///The gradient of the given observation index
    let obsGradient paramVec obsIdx = 
        let classProbs = classProbabilities paramVec obsIdx
        let choiceProbs = conditionalProbabilities paramVec obsIdx
        
        //gradients in LC are weighted by class
        let weights = Array.init nClasses (fun c -> classProbs[c] * choiceProbs[c][0]) |> normalizeSumTo1
        
        //class gradients
        let classGrad = 
            let varArr = Array.init nClasses (fun c -> data[obsIdx].ClassData[c] |> fst)
            let wAvgVars = Array.map2 (*) varArr classProbs |> vecSum
            Array.map2 (fun vars weight -> (vars - wAvgVars) * weight) varArr weights |> vecSum
        
        //choice gradients
        let choiceGrad = Array.init nClasses (fun c ->
            let vars, _ = data[obsIdx].ChoiceData[c]
            let row0 = new vector(vars.Cols, fun i -> vars[0, i])
            (-1.0*(vars.T * choiceProbs[c]) + row0)*weights[c]) |> vecSum

        (classGrad + choiceGrad).Evaluate()
    
    ///The number of observations
    let nObs = data.Length
    let weights = data |> Array.map (fun o -> o.Weight)

    interface ICostFunction with

        ///Log likelihood for all observations
        member _.Value paramVec = 
            PSeq.init nObs (obsLogLikelihood paramVec) 
            |> PSeq.withDegreeOfParallelism parallelism
            |> PSeq.map2 (*) weights 
            |> PSeq.sum

        ///Gradient for all observations
        member _.Gradient paramVec = 
            (PSeq.init nObs (obsGradient paramVec) 
            |> PSeq.withDegreeOfParallelism parallelism
            |> PSeq.map2 (*) weights 
            |> vecSum).Evaluate()

        ///Sum of scores matrix for all observations
        member _.SumOfScoreMatrix (paramVec:vector, gmean:vector) =
            (PSeq.init nObs (obsGradient paramVec)
            |> PSeq.withDegreeOfParallelism parallelism
            |> PSeq.map (fun g -> let g2 = g - gmean
                                  g2 * g2.T)
            |> PSeq.map2 (*) weights
            |> matSum).Evaluate()

        ///Numerical evaluation of Hessian matrix
        member this.Hessian (x:vector): matrix = 
            let H = new matrix(x.Length, x.Length)
            let eps = 1e-8
            for r in 0 .. x.Length-1 do
                let x1 = Vector.Copy(x)
                x1[r] <- x[r] + eps
                let grad1 = (this:>ICostFunction).Gradient(x1)
                x1[r] <- x[r] - eps
                let grad2 = (this:>ICostFunction).Gradient(x1)
                let dgrad = ((grad1 - grad2) / (2.0 * eps)).Evaluate()
                for c in 0 .. x.Length-1 do
                    H[r, c] <- dgrad[c]
            H


    
///Create the data needed to initialize a LatentClassLogLikelihood object for estimation.
let makeLCLLCostFunc (ps:Parameters) (observations:'a seq) (classDataFunc:'a*int->Variable seq) 
                     (choiceDataFunc:'a*int->((Variable seq)*float) list) (weightFunc:'a->float)
                     (degParallelism:int) (callback:bool->unit) : ICostFunction =
    
    ///Tracking array for if the estimate variables have been observed in the data
    let seen : bool[] = Array.create ps.estCount false

    //get class estimated variables and fixed utilities for one agent/latent class combo
    let makeClassData (obs:'a) (c:int) = 
        let estVec = new vector(ps.estCount)
        let mutable fixedU = 0.0

        for (name, mats) in classDataFunc (obs, c) do
            let v = mats |> Seq.sumBy (fun (scale, _, mat) -> scale * mat[0])
            let p = ps.getParameter name
            if p.Estimate then
                seen[p.EstIndex] <- true
                estVec[p.EstIndex] <- estVec[p.EstIndex] + v
            else
                fixedU <- fixedU + (v * p.Value)
                
        estVec, fixedU

    
    //get choice estimated variables and fixed utilities for one agent/latent class combo
    let makeConditionalChoiceData (obs:'a) (c:int) = 
        let observations, corrL = choiceDataFunc(obs, c) |> List.unzip

        let estMat = new matrix(observations.Length, ps.estCount, Array.zeroCreate (observations.Length*ps.estCount))
        let fixedU = new vector(corrL.Length, Array.ofList corrL) //corrections added to fixed utility

        observations |> List.iteri (fun row obs ->        
            for (name, mats) in obs do
                let v = mats |> Seq.sumBy (fun (scale, _, mat) -> scale * mat[0])
                let p = ps.getParameter name
                if p.Estimate then   
                    seen[p.EstIndex] <- true
                    estMat[row, p.EstIndex] <- estMat[row, p.EstIndex] + v
                else
                    fixedU[row] <- fixedU[row] + (v * p.Value)
        )
        estMat, fixedU
    

    //get the class and choice data for one agent's choiceset for all latent classes
    let makeDataForObs (obs:'a) =
        try
            let classData = Array.init ps.nClasses (makeClassData obs)
            let choiceData = Array.init ps.nClasses (makeConditionalChoiceData obs)
            let weight = weightFunc obs
            callback(true)
            Some({ ClassData = classData; ChoiceData = choiceData; Weight = weight })
        with
        | _ -> 
            callback(false)
            None

        
    let data = observations
                |> PSeq.withDegreeOfParallelism degParallelism 
                |> PSeq.choose makeDataForObs 
                |> PSeq.toArray

    //check that all estimable variables were observed in the data
    if not(Array.forall id seen) then 
        let unseen = Seq.zip ps.estParams seen 
                        |> Seq.filter (snd >> not) |> Seq.map fst |> Array.ofSeq 
        raise(Exception($"""Cannot estimate! Missing variables: {String.concat ", " unseen}"""))

    LatentClassLogitLL(degParallelism, ps.nClasses, data)
    