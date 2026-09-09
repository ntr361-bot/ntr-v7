# P9-P13 Reasoning and Decision Design

## Scope

This design implements the existing P9-P13 Macro Reasoning stages without adding architecture concepts. All components are deterministic sidecars. They do not register in production dependency injection, write prediction history, invoke Gating, rank zodiac signs, alter formal V7/V6.5, run COUNTER, or publish cloud output.

The chain is:

`P7 Observation → P8 Hypotheses → P9 Support → P10 CounterEvidence → P11 Critic → P12 Confidence Score → P13 Decision`

P13 ends at an immutable decision record. It does not execute a prediction.

## P9 supporting evidence

`MacroEvidenceEngine` reads only the frozen `MacroObservationSnapshot`, matching P8 hypothesis set and matching `PrefixContext`. It searches the following predeclared facts:

- ColdReturnIncreasing: `ColdReturnRate` 20 versus its 100-period baseline. P7 V1 does not currently produce this fact, so absence is recorded rather than inferred.
- HotPersistenceIncreasing: 20-period `ZodiacConcentration` versus its frozen 100-period baseline; support threshold is +0.02.
- TrendSignalDegrading: 20-period `TrendReliability` versus baseline; support threshold is -0.05. P7 currently marks this signal missing.
- RepeatRegimeIncreasing: 20 versus 100 for Immediate/Gap1/Gap2 repeat rates; support threshold is +0.05.
- ModelPerformanceShift: per-expert absolute change in Top6Rate between 20 and 100 periods; support threshold is 0.10.
- RandomFluctuation and NoMeaningfulChange: supported when the completed material-shift scan finds no non-null structural support.

Thresholds and definitions are frozen under `p9-evidence-v1`. Evidence reliability is a bounded sample-support factor `min(1, EffectiveSamples/100)`, not a claim of causal or historical reliability. Nested windows remain the same dependency group and are not counted as independent votes.

`EvidenceSearch.Completed=true` means every declared lookup was attempted. Missing facts remain in `MissingInputs`; an empty evidence list never means the search was skipped.

## P10 counter-evidence

`MacroCounterEvidenceEngine` actively checks the opposite interpretation for each candidate under `p10-counter-v1`:

- structural hypotheses are challenged when the corresponding 20-period delta is opposite or when available 50/100-period evidence does not confirm the short-window direction;
- ModelPerformanceShift is challenged when the 50-versus-100 shift is below 0.05 or points against the 20-period shift;
- RandomFluctuation and NoMeaningfulChange are challenged when at least one material non-null structural shift exists;
- unavailable facts are recorded in `MissingInputs`, never converted to zero.

Counter-evidence IDs bind the hypothesis, source facts, observation hash and definition version. P10 rejects an incomplete P9 search and cannot use target or future results.

## P11 reasoning critic

`MacroReasoningCritic` implements the ten existing checks with fixed policy `p11-critic-v1`: prefix safety, hypothesis/null-alternative presence, support search completion, counter-search completion, weight validity, sample sufficiency, short-window chasing, opposing/long-window evidence, action magnitude, and random-fluctuation consideration.

Hard invalidity, leakage, incomplete counter search, malformed weights, missing hypothesis candidates or action magnitude above 0.05 returns `Reject` with maximum magnitude 0. A valid proposal returns `Caution` with maximum magnitude 0.01 when evidence samples are below 20, evidence is only short-window, counter strength is at least support strength, a long-window counter exists, or proposed magnitude exceeds 0.01.

The frozen P11 interface does not yet carry P14's ranking-impact preview or a validated similar-environment statistic. P11 therefore records `RANK_IMPACT_PREVIEW_UNAVAILABLE` and `ENVIRONMENT_SIMILARITY_UNAVAILABLE` and cannot honestly emit `Pass` before those inputs exist. The `Pass` branch remains implemented for later compatible input expansion, but current P9→P13 execution is at most `Caution`. This is an explicit safety boundary, not a failed check disguised as success.

The critic does not change weights.

## P12 confidence score

`MacroConfidenceEngine` produces an explicitly versioned score, not a calibrated probability. Components are bounded support strength, inverse counter strength, sample sufficiency, available historical hypothesis reliability, critic factor, and available calibration quality. Missing components remain null and are excluded rather than treated as success.

The score is the equal-weight mean of available components, capped at 0.60 until at least 50 mature calibration samples exist, capped at 0.80 with calibration, capped at 0.60 for Critic Caution, and forced to 0 for Critic Reject or incomplete evidence searches. Version is `p12-confidence-score-v1`.

## P13 decision

`MacroDecisionEngine` validates but does not create the external `ActionProposal`. A hypothesis is selected only when its normalized supporting strength exceeds its counter strength. Null explanations may justify HOLD but never APPLY.

The deterministic policy `p13-decision-v1` is:

- Critic Reject → REJECT, applied weights equal before.
- No hypotheses, incomplete searches, no actionable selected hypothesis, confidence below 0.60, zero proposal, or null explanation not weaker than action evidence → HOLD, applied weights equal before.
- Otherwise APPLY. Critic Caution scales the move from Before toward Proposed so total-variation action magnitude is at most 0.01. Pass applies the proposal, still limited by the critic maximum.

For HOLD and REJECT, action magnitude is zero. Proposed weights are preserved for later counterfactual evaluation. All weight sets are finite, non-negative, have identical keys and sum to one within 1e-9.

## Acceptance

Tests must cover support and missing facts, active counter search, no future results, critic veto/caution/pass, confidence caps, HOLD and REJECT invariants, scaled APPLY, deterministic results, P7→P13 integration, and unchanged formal V7 Top6. Unified acceptance runs P5-P13 focused suites, Macro contracts, complete smoke regression and Release build.
