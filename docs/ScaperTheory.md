The Scaper model
=================

_This text is from the PhD thesis [Dynamic travel behaviour modelling](https://www.diva-portal.org/smash/record.jsf?pid=diva2%3A1913292&dswid=7022) by Stephen McCarthy ([@mccsteve](https://github.com/mccsteve)). It describes the mathematics behind the Scaper model implemented in this repository. Section and equation numbers are in the original document._

---------


Scaper is "a dynamic discrete choice model for activity generation and scheduling that features classic time-geography properties within a microeconomic framework" (Jonsson et al., [2014](https://doi.org/10.1068/b130069p)). The theory was first developed by Karlström ([2005](https://trid.trb.org/View/759266)), based on the work of Rust ([1987](https://doi.org/10.2307/1911259)) on dynamic models, and Jonsson & Karlström ([2005](https://urn.kb.se/resolve?urn=urn:nbn:se:kth:diva-71723)) presented an early proof-of-concept prototype. Two keys to implementing larger-scale models came from Fosgerau et al. ([2013](https://doi.org/10.1016/j.trb.2013.07.012)), who found a computationally feasible estimation method, and Saleem et al. ([2018](https://doi.org/10.1016/j.procs.2018.04.090)), who incorporated importance sampling of locations. Building on these theoretical insights, Blom Västberg et al. ([2020](https://doi.org/10.1287/trsc.2019.0898)) presented the first city-scale version of Scaper, estimated and validated through simulation results. This section presents the Scaper activity-based microsimulation model up to Blom Västberg et al.; the contributions of this thesis will be detailed in later sections.

A Scaper agent starts in a particular state &mdash; for example, having just woken up at home at the start of a day. They then proceed forward in time, making sequential choices about what to do, where to do it, and how to get there. These attributes are decided jointly. For instance, after having made several decisions to stay at home, an agent may at some time choose to depart to go shopping at the nearby grocery store on foot. They may instead decide to depart to work at their usual work location by public transit, or any other state reachable from their current state. The choice to start a new activity usually comprises activity purpose, destination and travel mode, but the specific elements to represent are up to the modeller. When the agent makes a choice, they move to a new state dependent on their previous state, their choice and external conditions (e.g., travel times). These external conditions may be stochastic, for instance to represent travel time variability. Figure 1 illustrates an agent's movement through the state space.

<p>
<img src="figs/statespace.svg" width="500" alt="Representation an agent's possible movement through a simplified representation of a possible Scaper state space">
<br/><em>Figure 1: Representation an agent's possible movement through a simplified representation of a possible Scaper state space.</em>
</p>

A foundational aspect of the model is that its agents are forward-looking. Scaper agents are not simple hedonists who choose the alternative which gives them the highest short-term benefit. Instead, they assess both the expected immediate effect of their choice and the consequences of ending up in the anticipated state once the choice is made. A Scaper agent who must work will travel to work even if they do not enjoy the commute because skipping a day at work would ultimately be worse.

To turn the above concepts into a rigorous and computationally feasible model, Scaper is a random utility maximization model situated within the tradition of discrete choice modelling. The immediate benefit of a choice in Scaper is represented by a real-valued utility, comprising an (explicitly modelled) observed part and a random variable with a multivariate extreme value (MEV) distribution. Making agents forward looking is conceptually straightforward in the MEV context: the utility that the agent maximizes when making a choice is the sum of the immediate expected utility of that choice and the expected maximum potential utility an agent could obtain in the resulting state. This expected maximum potential utility is called the value function of the state. This takes some care to operationalize as discussed below.

As Jonsson et al., ([2014](https://doi.org/10.1068/b130069p)) shows, the Scaper model has a close connection to the theory of time geography (Hägerstrand, [1970](https://doi.org/10.1111/j.1435-5597.1970.tb01464.x)). In that theory, individuals' movement in space and time is conditional on certain constraints, for example, the need to be at work at a certain time, to pick up children from daycare, or to return home at the end of the day. These spatio-temporal constraints do not only affect individuals' behaviour at the specified times but throughout the day: a person who must be home by 10pm, for instance, will not choose to travel at 9pm to a place more than an hour away from home. Scaper endogenously represents agents' space-time prisms, respecting constraints in time and space as defined by the modeller. This will be explained mathematically below; the key is that the agent has zero probability of reaching states which do not meet the constraints (being at home after 10pm) and taking actions which can only reach these constrained states (travelling too far away from home at 9pm).


## Theoretical foundations

The theoretical description of Scaper starts with an agent moving through a Markov Decision Process (MDP; see Howard, [1960](https://books.google.se/books/about/Dynamic_programming_and_Markov_processes.html?id=fXJEAAAAIAAJ&redir_esc=y)). This context imposes the limitation that the agent makes a decision only based on their current state; while on first glance this seems to place unrealistic limitations on agent decision-making, elements of the agent's history can be `remembered' and used to guide behaviour via their inclusion as state variables. The description below uses subscript $i$ to index states and actions in sequential (chronological) order. Time is a continuous variable in the state space; $t_i$ is the time of state $s_i$. Following common MDP notation,
- the state space is $S$ and the set of possible actions from state $s_i$ is $A_{s_i}$, 
- the state transition function, i.e. the probability that action $a_i$ from state $s_i$ will result in $s_{i+1}$, is $P(s_{i+1} \mid s_i, a_i)$, and 
- the immediate reward for transitioning from $s_i$ to $s_{i+1}$ due to $a_i$ is $R(s_i,a_i,s_{i+1})$.

Jonsson & Karlström ([2005](https://urn.kb.se/resolve?urn=urn:nbn:se:kth:diva-71723)) present finite-horizon and infinite-horizon theoretical versions of the dynamic discrete choice model. The finite-horizon model is the focus of most Scaper research as well as all papers in this thesis, and is thus the one described here. There exists a set of absorbing end states $S_\text{end}$ in the MDP which terminate the agent's path through the state space. These represent situations where the agent is finished their activity schedule, such as being at home at the end of a day. Future discounting of utility is not considered. The reward of a path through the state space is the sum of the rewards of the transitions on the path.

Agents in the model are utility maximizers: they identify a policy $\pi^*:S \rightarrow A$ for movement through the state space which maximizes their expected reward. This is defined by the value function $V(s_0)$ of a state $s_0$, which is the maximum expected utility of the path leading from that state to an end state:
    
```math
V(s_0) = \max_{\pi} \mathop{\mathbb{E}} \left\{ \sum_{i=0}^{n-1} R(s_i, a_i, s_{i+1}) \; \middle| \; \pi(s_i) = a_i, \; s_n \in S_\text{end} \right\}
```
    
where the expectation is taken over $P(s_{i+1} \mid s_i, a_i)$. For states in $S_\text{end}$, the value function is defined to be zero, and if a state has no possible path to $S_\text{end}$ then it has $-\infty$ value function. Following Bellman ([1957](https://www.jstor.org/stable/24900506)), the value function can be expressed as a Bellman equation:
    
```math
V(s_i) = \max_{a_i} \left\{ P(s_{i+1} \mid s_i, a_i) \left[ R(s_i, a_i,s_{i+1}) + V(s_{i+1}) \right] \right\} 
```

It is worth at this point introducing notation for the expected reward of an action, $R(s_i, a_i)$, and the expected value function after the action, $EV(s_i, a_i)$:
  
```math
R(s_i, a_i) = \mathop{\mathbb{E}}\left[ R(s_i, a_i, s_{i+1}) \right]
```
  
```math
EV(s_i, a_i) = \mathop{\mathbb{E}} \left[ V(s_{i+1}) \, | \, s_i, a_i \right]
```

where both expectations are taken over $P(s_{i+1} \mid s_i, a_i)$. With these definitions, we can simplify the Bellman equation of the value function to:

```math
V(s_i) = \max_{a_i} \left\{  R(s_i, a_i) +  EV(s_i, a_i) \right\}
```

Actions must have non-trivial durations. Formally, there exists a $\delta>0$ such that if the agent moves from state $s_i$ to state $s_{i+1}$ then $t_{i+1}-t_i\geq\delta$, for all such $s_i$ and $s_{i+1}$. This ensures that paths through the state space to $S_\text{end}$ have finite length. It is an open question how removing this assumption (i.e. allowing for continuous-time actions) would impact the model and whether it would be calculable.

### Dynamic discrete choice model

Using discrete choice theory, Rust ([1987](https://doi.org/10.2307/1911259)) shows how to turn the MDP into a dynamic RUM model. As mentioned in Section 2.1, RUM models are supported by a well-established theory which brings several modelling advantages. Scaper specifically uses the multinomial logit model (MNL) introduced by Mcfadden ([1974](https://escholarship.org/content/qt61s3q2xr/qt61s3q2xr.pdf)). This assumes that the reward $R$ can be additively separated into observed term $u$ and state-independent unobserved term $\varepsilon_i$:

```math
R(s_i, a_i, s_{i+1}) = u(s_i, a_i, s_{i+1}) + \varepsilon_i(a_i)
```

It also assumes the $\varepsilon_i(a_i)$ variables are independent and identically distributed with a Gumbel extreme value distribution with zero mean. This assumption leads to a concise closed-form solution for both expected value function and model probabilities.

Like above, the expected observed utility of an action $u_a(s)$ is denoted:

```math
u(s_i, a_i) = \mathop{\mathbb{E}}\left[ u(s_i, a_i, s_{i+1}) \right]
```

where the expectation is again taken over $P(s_{i+1} \mid s_i, a_i)$. The `full' observed utility of an action can now be expressed simply as $u(s_i, a_i) + EV(s_i, a_i)$. The expected value function of state $s_i$ taken over $\varepsilon_i$ is denoted $\bar V(s_i)$ and is given by the logsum:

```math
\bar V(s_i) = \mathop{\mathbb{E}}_{\varepsilon_i}\left[ V(s_i) \right] = \log \sum_{a_i \in A_{s_i}} e^{u(s_i, a_i) + EV(s_i, a_i)}
```

and the expected value $EV$ of an action from (4) is now:

```math
EV(s_i, a_i) = \int \bar{V} (s_{i+1}) dP(s_{i+1} \mid s_i, a_i)
```

We can theoretically calculate (8) for a given state using dynamic programming, either using backward induction (starting with $S_\text{end}$ which have $\bar V=0$) or recursively from $s_i$.

When simulating an agent's path through the state space, the probability of taking action $a_i$ in state $s_i$ is the familiar MNL formulation:

```math
P(a_i \mid s_i) = \frac{e^{u(s_i, a_i) + EV(s_i, a_i)}} {\sum_{a_i' \in A_{s_i}} e^{u(s_i, a_i') + EV(s_i, a_i')}} = \frac{e^{u(s_i, a_i) + EV(s_i, a_i)}} {e^{\bar V(s_i)}}
```

It is now possible to understand how the model endogenously respects spatio-temporal constraints as mentioned above. First, recall that a state that has no possible path to $S_\text{end}$ has a negative infinite value function. In practice, there are two types of these `bad' states. The first are easily identifiable as not being able to reach $S_\text{end}$; usually the set of end states is defined so that they exist at a certain time $\tau$, so these are states not in $S_\text{end}$ with $t \geq \tau$. These bad states are defined to have $\bar V = -\infty$; the recursive calculation of the value function in (8) stops when it reaches a state in $S_\text{end}$ or a state with $t>\tau$. In the second type of bad state, casual inspection does not reveal a problem, but any actions that the agent can take from that state have an expected value $EV(s_i, a_i)$ of $-\infty$, i.e. there are no actions that can be guaranteed not to reach another bad state. By (8), these states also have $\bar V = -\infty$. From (10) it can be seen that the probability of the agent taking an action that has a non-zero probability of transitioning to a bad state is zero. The non-bad states represent the agent's space-time prism and are the only states a Scaper agent visits.

A question common of all discrete choice models is whether the distribution of the error terms is adequate to represent real world behavioural variation and substitution patterns among alternatives. The MNL model with its i.i.d. Gumbel $\varepsilon$ error terms has the property of independence of irrelevant alternatives (IIA), which is often violated in real-life choice situations by unobserved correlation between alternatives. It is a subject of ongoing research to what extent the use of MNL choice probabilities in Scaper limits the model's representation of real-world behaviour and substitution patterns. The inclusion of the expected future value of an action in the MNL decision utility captures some of the correlation between similar alternatives, moving what would have been unobserved correlation into the observed utility function. This means the model does not respect IIA in terms of changes to travel times or location characteristics. Blom Västberg ([2018](https://urn.kb.se/resolve?urn=urn:nbn:se:kth:diva-219882)) discusses this topic in more detail and presents possible alternatives to the MNL choice probabilities in the dynamic discrete choice context. This thesis restricts itself to the MNL formulation presented above, which has been validated across various settings, proving sufficient for the modelling requirements.


## Modelling practice

The model as described above is purposefully general; for instance, the state space has not been defined save for including a time component. Defining the state and action spaces are up to the modeller for any specific application, and each of the papers in this thesis has a slightly different definition according to its modelling goals. However, different versions of Scaper have similarities:
- States are generally comprised of time, location (index in a zone system), activity purpose and some history variables (described below).
- Actions include continuing an activity and starting a new activity, comprising of a joint choice of destination, travel mode and activity purpose. Continuing an activity moves the agent forward by a fixed timestep, usually 10 minutes. A new activity can be performed in the same zone as the previous activity, but not without performing some travel.
- Utility functions are typically linear in parameters and are split into travel, start activity and continue activity functions.
- The end states in $S_\text{end}$ are generally defined by requiring the agent to be home at a certain time horizon. One day is the most common time horizon and is used in the papers in this thesis, but multiple-day models are also possible (see, e.g., Blom Västberg ([2018](https://urn.kb.se/resolve?urn=urn:nbn:se:kth:diva-219882)).

### History state variables

As mentioned at the start of this section, agent behaviour in Scaper must obey the Markov property that transition probabilities are independent of prior states. People's behaviour is clearly dependent on what they have already done; for instance, a person who has just eaten at a restaurant and returned home is presumably less likely to immediately go out to another restaurant. To represent this path-dependence in an MDP, the state space is expanded using history state variables. How to define and use these variables is up to the modeller and can vary depending on the purpose of the modelling project. Possible examples include tracking whether the agent has been to work, whether they have picked up their kids from daycare, and which vehicle they have taken on a tour. However, there is an incentive toward simplicity, as adding new state variables multiplies the size of the state space and thus the computational and memory requirements of calculating value functions.

There are two uses of history variables in Scaper models. First, they can be used to influence agents' behaviour through the utility function, which is useful to reflect the path-dependence of real-world travel choices. For instance, making an agent's mode choice conditional on previous mode choices (or a proxy, such as the vehicle they took when leaving home) can help represent modal consistency throughout the day.

The second way in which history variables are used is to define spatio-temporal constraints on the agent's behaviour. This is done by defining the end-states in $S_\text{end}$ so that desired history variable values must be achieved. If we assume the agent must return home by time $\tau$, then any state with $t \geq \tau$ that does not have the correct combination of history variables as defined by the modeller will have $\bar V = -\infty$. Any state which can only reach these states and not $S_\text{end}$ will therefore also have $\bar V = -\infty$ and be reached with zero probability. As an example, imagine that the modeller decides that Alice must go to work for at least 8 hours and return home by 11pm. The modeller includes a work duration history variable $d_w$ that increments when Alice continues a work activity, and defines $S_\text{end}$ such that it only contains states representing being at home with time 11pm and $d_w \geq \text{8h}$. In simulation, Alice's choices will depend on the specific utility function but will always include spending at least 8 hours at work, and her decisions around performing non-work activities will be shaped by this requirement.


### Implementation

While Scaper is not tremendously theoretically complex, the expected value functions as described above are infeasible to calculate for full-day schedules at the scale of an entire city. Indeed, much of the difficulty of implementing Scaper at scale is the need to calculate a recursive value function with a large state space. For example, in the model developed in Blom Västberg et al. ([2020](https://doi.org/10.1287/trsc.2019.0898)) there were approximately  $4 \cdot 10^8$ links (actions) in the state space. All implementations of Scaper make some compromises to achieve a model with computable value functions.

The biggest simplification made in all current Scaper implementations is that state transitions are deterministic, i.e. travel times are fixed. It has been an elusive development goal to implement a model with stochastic transitions; estimation becomes very difficult as discussed in Section 3.3. For the papers in this thesis, the probability function $P(s_{i+1} \mid s_i, a_i)$ equals 1 for one particular state and 0 otherwise. This effectively takes out the expectation calculations, making the value function into:

```math
\bar V(s_i) = \log \sum_{a_i \in A_{s_i}} e^{u(s_i, a_i) + \bar V(s_{i+1})}
```

The second concession to computational feasibility made by all current implementations is to approximate the value function over discrete timesteps, as introduced by Blom Västberg et al. ([2020](https://doi.org/10.1287/trsc.2019.0898)). Even if the choice to continue an activity moves an agent forward by 10 minutes, agents make more than a hundred decisions over the course of day. Since travel times are continuous, the exponential nature of the recursive value function puts calculating the actual value function out of reach. To make calculation possible, time is measured in units of the continue-activity steps, and approximated $\hat V(s_i)$ is used:

```math
\hat{V}(s_i) = \log \sum_{a_i \in A_{s_i}} e^{u(s_i, a_i) + (1-\alpha) \hat V(\lfloor s_{i+1} \rfloor) + \alpha \hat V(\lceil s_{i+1} \rceil)}
```

where $\lfloor s_{i+1} \rfloor$ is $s_{i+1}$ with time $\lfloor t_{i+1} \rfloor$, and $\lceil s_{i+1} \rceil$ is $s_{i+1}$ with time $\lceil t_{i+1} \rceil$, and $\alpha = t_{i+1}-\lfloor t_{i+1} \rfloor$ is the fractional time past the previous timestep. When value functions for integral timesteps are calculated, they are cached to avoid recalculation.

Prior to this thesis, Scaper was implemented in C\# .NET (Blom Västberg, [2018](https://urn.kb.se/resolve?urn=urn:nbn:se:kth:diva-219882)). Since C\# language does not support efficient (tail-optimized) recursion, that implementation uses a backward induction algorithm to perform the calculation of value functions. Since the limiting computational factor is the calculation of links from all-to-all destinations, with millions of links at each timestep, the implementation treats these internally as one transition with a matrix of travel times and utilities. This approach is much more efficient and does not affect the calculated values. In the C\# implementation, these matrix calculations are optimized using IntelMKL and a custom C++ library.

Even with the simplifications discussed above and an optimized value function calculation, calculating full value functions takes seconds or even minutes per agent per core depending on the magnitude and connectedness of the state space. The importance sampling of zones, or location sampling Saleem et al. ([2018](https://doi.org/10.1016/j.procs.2018.04.090)), is often used to reduce the number of zones considered, thus reducing the size of matrices in calculations. Location sampling reduces the time taken to calculate the value functions for all states to fractions of a second per agent.


## Estimation

Discrete choice models for travel behaviour rely on the ability to consistently estimate parameters for the utility function using observed data. It is the estimation of these parameters that separate the models from guesswork and ensure they can accurately recreate observed behaviour. The vector of real-valued parameters is notated $\boldsymbol{\theta}$. Observed data generally contains observations of many individuals, while the sections above have made the assumption of one agent, omitting the agent from the notation. This section considers individual $n$ with attributes that can be used in the utility function. The probability of this agent taking an action from (10) is now more fully notated $P(a_{ni} \mid s_{ni}, \boldsymbol{\theta})$.

If individual $n$ has observed path $`\boldsymbol{\zeta}_n = \{s_{n1}, a_{n1},s_{n2}, ... \, a_{nk_n}, s_{n(k_n+1)}\}`$, then the likelihood of $\boldsymbol{\zeta}_n$ according to the parameterized model is:

```math
L_n(\boldsymbol{\theta}; \boldsymbol{\zeta}_n) = \prod_{i=1}^{k_n} P(a_{ni} \mid s_{ni}, \boldsymbol{\theta}) \cdot P(s_{n(i+1)} \mid s_{ni}, a_{ni})
```

It is worth noting that (13) presents the likelihood using the full stochastic-transition version of Scaper from (8) and (10).  Assuming the model is to be estimated using observation set $\mathcal{O}$ consisting of $N$ individuals, the log likelihood function is:
```math
    \mathcal{L}\mathcal{L}(\boldsymbol{\theta}; \mathcal{O}) = \sum_{n=1}^N \log L_n(\boldsymbol{\zeta}_n \mid s_{n1}, \boldsymbol{\theta})
```

Using $\log L_n$ and its gradient, it is theoretically possible to to find a likelihood-maximizing parameter vector $\boldsymbol{\theta}^*$ using standard optimization algorithms. The function $\log L_n$ is calculable and, with the simplifications discussed in Section~\ref{subsection:scaper-modelling-practice}, can even be calculated once per agent in simulation with appropriate parallelization. However, it is prohibitive to calculate $\log L_n$ and its gradient for each update of $\boldsymbol{\theta}$ that would be required for parameter estimation, even for a moderately sized dataset.

This problem was solved by Fosgerau et al. ([2013](https://doi.org/10.1016/j.trb.2013.07.012)) for a dynamic discrete choice model like Scaper under two conditions: (a) that $\varepsilon$ terms are i.i.d. Gumbel distributed, and (b) that state transitions are deterministic. Given these conditions, they observed that the problem could be transformed into a simpler MNL model over paths through the state space. Given individual $n$ with observed path $\boldsymbol{\zeta}_n$ as defined above and the deterministic version of Scaper from (11), then: 

```math
    \begin{split}
     L_n(\boldsymbol{\theta}; \boldsymbol{\zeta}_n) & = \prod_{i=1}^{k_n} e^{u(s_{ni}, a_{ni} \mid \boldsymbol{\theta}) + \bar V(s_{n(i+1)} \mid \boldsymbol{\theta}) - \bar V(s_{ni} \mid \boldsymbol{\theta})} \\
     & = e^{\left[ \sum_{i=1}^{k_n} u(s_{ni}, a_{ni} \mid \boldsymbol{\theta}) \right] + \bar V(s_{n(k_n+1)} \mid \boldsymbol{\theta}) - \bar V(s_{n1} \mid \boldsymbol{\theta})}
     \end{split}
```

Since the path terminates at $s_{n(k_n+1)}$, that state must be an element of $S_\text{end}$ and thus $\bar V(s_{n(k_n+1)} \mid \boldsymbol{\theta})=0$. As well, $\bar V(s_{n1} \mid \boldsymbol{\theta})$ is the expected maximum utility of agent $n$ being in state $s_{n1}$, i.e. the expected maximum utility of all possible paths from $s_{n1}$ to $S_\text{end}$. For tidy notation, let $`U(\boldsymbol{\zeta}_n \mid \boldsymbol{\theta}) = \sum_{i=1}^{k_n} u(s_{ni}, a_{ni} \mid \boldsymbol{\theta})`$ and let the set of all paths from $s_{n1}$ to a state in $S_\text{end}$ be $Z(s_{n1})$. Then the individual likelihood can be rewritten as:

```math
    L_n(\boldsymbol{\theta}; \boldsymbol{\zeta}_n) = \frac{e^{U(\boldsymbol{\zeta}_n \mid \boldsymbol{\theta})}}{\sum_{\boldsymbol{\zeta}'_n \in Z(s_{n1})} e^{U(\boldsymbol{\zeta}'_n \mid \boldsymbol{\theta})}}
```

This procedure reduces the computationally expensive dynamic model to an MNL over paths which is used for parameter estimation.

The state space in a typical Scaper model is large and well-connected enough that the set $Z(s_{n1})$ is far too large to generate and use directly for estimation. Considering that an agent makes more than 100 choices, each of which can have thousands of alternatives, Blom Västberg et al. ([2020](https://doi.org/10.1287/trsc.2019.0898)) estimate that direct estimation of a relatively simple model specification would take around 1\,000 days on a single processor. Estimating Scaper is therefore performed using a sampled choice set constructed with importance sampling, which McFadden ([1978](https://elischolar.library.yale.edu/cgi/viewcontent.cgi?article=1709&context=cowles-discussion-paper-series)) shows produces asymptotically unbiased estimates. It is also for this reason that it is not possible to treat the model as an MNL over paths to generate simulated paths.

The construction of this choice set requires a generating process, and hopefully one that produces fairly likely alternative paths so that the choice set provides a good basis for discrimination between observed and simulated paths. It can be problematic, for instance, for all of the simulated paths in the choice set to have too many activities; in this case, the parameters controlling activity participation will dominate the likelihood function and other parameters may not be meaningfully estimated. The typical procedure for estimating Scaper is to bootstrap the choice set using Scaper itself as the choice set generating process. This is done first with a `guesstimate' parameter vector and then with successive iterations of estimated parameter values. Blom Västberg et al. ([2020](https://doi.org/10.1287/trsc.2019.0898)) explains the process in more detail. Experience over the course of research for this thesis shows that this technique works quite well once the first functional estimate is made, though the initial parameter vector guess can be difficult to get right.
