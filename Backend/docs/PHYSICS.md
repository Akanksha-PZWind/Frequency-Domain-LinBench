# LinBench — physics and mathematics

What LinBench computes and why, with the equations exactly as implemented. Each section names the
code that implements it. For the code structure see [ARCHITECTURE.md](ARCHITECTURE.md).

> **Keep this file current.** When a formula, tolerance, threshold or modelling assumption changes
> in the code, change it here in the same edit (see `CLAUDE.md`).

---

## 1. What an OpenFAST linearisation is

OpenFAST linearises the coupled aero-servo-hydro-elastic model about an operating point (trim) and
writes, for each linearisation time,

```
ẋ = A x + B u,     y = C x + D u
```

x: continuous states (structural DOFs and their velocities, plus aero/hydro states), u: inputs
(wind, blade pitch, generator torque, …), y: outputs (GenSpeed, tower-top acceleration, …).
Each file also states the trim values (operating point of every channel) and, in its header, the
wind speed U, rotor speed Ω, azimuth ψ of blade 1 and the simulation time t.

Because the rotor turns, blade DOFs live in the **rotating frame** and the matrices depend on ψ:
A(ψ), B(ψ)… One operating point is therefore a set of files at several azimuths (typically 12 or 36)
of the same periodic steady state. *Code: `linfile.py`, `discover.py`.*

## 2. Grouping files into operating points

Files are grouped by the **wind speed in the header**, never by file name (sorting `lin_10` before
`lin_2` silently scrambles a sweep). Inside a group they are sorted by azimuth.

A wind speed is split into several operating points only when its **azimuths repeat** (two complete
sets): first by file-name root, then by gaps > 2 % in rotor speed. *Code: `discover.group_operating_points`.*

### 2.1 Rotor speed that differs between azimuths

All azimuths of one operating point should see the same equilibrium. With Ω_k, ψ_k, t_k of each
file, LinBench computes the peak-to-peak spread (max Ω − min Ω)/mean Ω and fits two explanations by
least squares, compared with the **adjusted** R² (so the 5-term fit gains nothing from its extra terms):

```
time trend:       Ω = a + b·t                                   (1 regressor)
azimuth pattern:  Ω = a + c₁cos ψ + s₁sin ψ [+ c₃cos 3ψ + s₃sin 3ψ]   (2 or 4 regressors; 3P if ≥ 7 files)
adjusted R² = 1 − (1 − R²)(n − 1)/(n − p − 1)
```

| Class | Rule | Physical meaning |
|---|---|---|
| drift | R²_time ≥ 0.8 and ≥ R²_azimuth | rotor still accelerating: trim not converged (dΩ/dt = b reported in rpm/s) |
| periodic | R²_azimuth ≥ 0.8 | 1P/3P ripple (tower shadow, shear) of a converged periodic trim: expected |
| scatter | neither | files from different runs or settings |

Levels: drift/scatter → warning ≥ 0.5 %, notice ≥ 0.1 %; periodic → warning ≥ 2 %, notice ≥ 0.5 %.
MBC always uses each file's own Ω, so periodic ripple is handled correctly. *Code: `discover.rotor_speed_check`.*

**Consequence check.** Each azimuth file is also MBC-transformed on its own (no averaging); for every
mode of the averaged model below 3 Hz the nearest eigenvalue of each single-azimuth model gives
Δf/f and Δζ. Large values mean the files disagree (anisotropy or different operating points). Whirl
modes react most because their frequencies move with ±Ω. *Code: `OpModel._azimuth_sensitivity`, `Session._az_sens`.*

## 3. Multi-blade coordinate (Coleman) transform

For each blade triplet q₁, q₂, q₃ (same DOF on blades 1–3) with ψ_b = ψ + 2π(b−1)/3:

```
q_rot = T₁(ψ) q_nr,   T₁ rows = [1, cos ψ_b, sin ψ_b]      q_nr = [q₀ (collective), q_c (cos), q_s (sin)]
T₂ rows = [0, −sin ψ_b, cos ψ_b],   T₃ rows = [0, −cos ψ_b, −sin ψ_b]
```

For second-order DOFs (position p, velocity v) the state transform and its time derivative are

```
T  = [[T₁, 0], [Ω T₂, T₁]]                         (rows/cols: p, v)
Ṫ = [[Ω T₂, 0], [Ω² T₃ + Ω̇ T₂, Ω T₂]]             (Ω̇ = 0 is used)
first-order triplets: T = T₁, Ṫ = Ω T₂
```

and the system in the fixed (non-rotating) frame is

```
A_nr = T⁻¹ (A T − Ṫ),   B_nr = T⁻¹ B T₁ᵤ,   C_nr = T₁ᵧ⁻¹ C T,   D_nr = T₁ᵧ⁻¹ D T₁ᵤ
```

(T₁ᵤ / T₁ᵧ act on blade input / output triplets such as pitch commands and root moments).
Channels that belong to no triplet are left untouched.

**Azimuth averaging.** For an isotropic rotor A_nr is constant; in general the N files are averaged,
Ā = (1/N) Σ A_nr(ψ_k) (same for B, C, D). Averaging removes the periodic part of the matrices; it does
**not** limit the bandwidth of the averaged model.

**Periodicity index** = max_k ‖A(ψ_k) − Ā‖ / ‖Ā‖, reported before MBC (raw matrices) and after.
After MBC it should drop by orders of magnitude; what remains is genuine anisotropy (or a
rotor-speed problem, §2.1).

**Azimuth state removal.** After averaging, nothing physical depends on absolute azimuth, but Ā keeps
small spurious ∂/∂ψ terms that pair the generator-azimuth *position* state with the rotor-speed state
into a fictitious real pole pair (one unstable). As ROSCO does (`trim_system(rm_azimuth=True)`), that
state is removed. *Code: `mbc.py`, `model.OpModel` (reference: G. Bir, AIAA 2008-1300).*

## 4. Modes

```
A v_k = λ_k v_k,   W = V⁻¹ (rows w_k: left eigenvectors)
f_n = |λ|/2π,   f_d = |Im λ|/2π,   ζ = −Re λ / |λ|        stable ⇔ Re λ < 0 for all λ
```

If V is ill-conditioned (cond > 1e13) left eigenvectors are computed directly and normalised.

**Participation factors** p_ik = |w_ki v_ik| / Σ_i |w_ki v_ik|, summed per physical DOF (position,
velocity and hydrodynamic states of the same DOF). The label is the DOF with the largest share.
Unlike the "largest eigenvector entry" rule, this does not depend on state units (metres vs radians).

**Rotor symmetry.** For modes dominated by blade DOFs (or with > 50 % rotor share): collective if the
collective components dominate, otherwise cyclic. For a cyclic pair with components C (cos) and S
(sin) of the eigenvector, forward whirl ⇔ |C + jS| ≫ |C − jS|; the participation-weighted index
Σ w (|C+jS| − |C−jS|)/(|C+jS| + |C−jS|) > 0.25 ⇒ FW, < −0.25 ⇒ BW. For an isotropic rotor the
FW/BW modes sit exactly at λ_rot ± jΩ (validated to 2e-8). *Code: `modal.analyse_modes`.*

**Campbell tracking.** Between neighbouring wind speeds, modes are paired by the optimal (Hungarian)
assignment on

```
cost_ij = (1 − MAC_ij) + 0.6 |ln(f_j/f_i)| + 0.3·[different family],   accept if cost < 0.8
MAC_ij = |v_iᴴ v_j|² / (‖v_i‖² ‖v_j‖²)   (on structural position components, normalised)
```

so lines follow mode shapes and do not jump where frequencies cross. 1P = Ω/2π and its multiples
are drawn for resonance checks. *Code: `modal.campbell`.*

## 5. Transfer functions and time responses

For input j and output i (b = B[:, j], c = C[i, :], d = D[i, j]):

```
G(s) = c (sI − A)⁻¹ b + d = Σ_k r_k / (s − λ_k) + d,    r_k = (c v_k)(w_k b)
```

A mode with r_k ≈ 0 (relative weight < 1e-6) is not excited by the input or not seen by the output;
its pole is cancelled by a zero and is shown separately. **Zeros** are the finite generalised
eigenvalues of the pencil ([[A, b],[c, d]], diag(I, 0)) on the significant modes. Right-half-plane
zeros (non-minimum phase, e.g. pitch → generator speed) limit the achievable bandwidth.

DC gain = d − Σ r_k/λ_k (∞ with an integrator); bandwidth = first frequency where |G| < |G(0)|/√2.

**Insights panel (Transfer function view, front end only).** Factored form
G(s) = K · Π(zero factors) / Π(pole factors), one factor per real root (s − p) or conjugate pair
(s² − 2 Re p · s + |p|²), s in rad/s, sorted by |root|; K = G(0) · Π D_i(0) / Π N_i(0) (shown as "K"
with an integrator or a zero at the origin). Rule of thumb shown with RHP zeros: keep the feedback
bandwidth below about half of the lowest RHP zero, ω_B ≲ |z|/2 (Skogestad & Postlethwaite,
*Multivariable Feedback Control*, ch. 5, limitations imposed by RHP zeros). "Lightly damped" lists up to three poles with 0 ≤ ζ < 5 %,
largest relative residue first.

**Insight levels (Loop shape and Stability summary panels, front end only).** Green / amber / red:
gain margin ≥ 6 dB / – / < 6 dB; phase margin ≥ 45° / 30–45° / < 30°; s_m ≥ 0.5 / 0.4–0.5 / < 0.4
(‖S‖∞ ≤ 6 dB is green); disk gain margin ≥ 6 / 3–6 / < 3 dB; peak of T ≤ 3 / 3–6 / > 6 dB; delay margin
≥ 5 / 2–5 / < 2 controller steps (only when the `.fst` gives DT); least-damped closed-loop mode
ζ ≥ 5 / 2–5 / < 2 %. "Limiting" margin / "weakest wind speed": the smallest of GM/6 dB, PM/45°, s_m/0.5
(shown as % of target). Crossover ω_c = frequency of the phase-margin crossing; loop time constant ≈ 1/ω_c.

**Frequency response** is evaluated from the modal form when cond(V) < 1e9, else by batched solves
on the Hessenberg form of A. **Time responses** are exact for a zero-order-hold input:
[A_d, B_d] = expm([[A, b],[0, 0]]·Δt). Step metrics: peak, 2 % settling, 10–90 % rise, overshoot,
initial undershoot. *Code: `tf.SISO`, `lti.freqresp`, `lti.step_impulse`.*

**Browser-side curves.** Plots are re-evaluated for the visible range from the half pole/residue form

```
G(jω) = d + Σ_{p real} r/(jω − p) + Σ_{Im p > 0} [ r/(jω − p) + r̄/(jω − p̄) ]
step:    y(t) = d + Σ w·Re[ r (e^{pt} − 1)/p ]   (r·t for p = 0),   impulse: y(t) = Σ w·Re[ r e^{pt} ],   w = 2 for pairs
phase:   θ(ω) = arg k + Σ_z φ(ω, z) − Σ_p φ(ω, p),   φ = continuous arg(jω − q),  snapped onto arg G
```

The branch is fixed so that θ(1e-3 rad/s) ∈ (−π, π]. Every export is checked against the state-space
result before it is used (§ ARCHITECTURE 4.1). *Code: `lti.modal_export`, `app.js MOD`.*

## 6. Controllability, observability, Hankel singular values

**PBH test:** mode k is uncontrollable ⇔ w_k B = 0, unobservable ⇔ C v_k = 0. Graded measures
(Hamdan & Nayfeh 1989), cosines in [0, 1]:

```
c_k = |w_k B| / (|w_k| ‖B‖),   o_k = |C v_k| / (‖C‖ |v_k|),   PBH fail if < 1e-7
modal Hankel value h_k = |C v_k| |w_k B| / (2 |Re λ_k|)   (coordinate-free)
```

**Ranks** come from the orthogonal staircase (Kalman decomposition, Van Dooren 1981) on a
diagonally balanced A with unit-norm B columns / C rows, tolerance 1e-9·max(1, ‖A‖), not from the
ill-conditioned [B AB A²B …]. The eigenvalues left in the uncontrollable / unobservable block are
reported by name; stabilisable/detectable ⇔ all of them are stable. Minimal order = observable part
of the controllable part.

**Hankel singular values** of the stable part. Unstable/marginal modes (Re λ ≥ −1e-8) are split off
first (ordered real Schur form + Sylvester equation, as MATLAB `hsvd`) and counted separately:

```
A W_c + W_c Aᵀ + B Bᵀ = 0,   Aᵀ W_o + W_o A + Cᵀ C = 0,   σ_i = √λ_i(W_c W_o)
balanced truncation to order r:  ‖G − G_r‖∞ ≤ 2 Σ_{i>r} σ_i
```

HSVs depend on channel units (display units: deg, deg/s, …). *Code: `structure.py`.*

## 7. The ROSCO controller as a linear system

Built from DISCON.IN at each operating point; continuous time. *Code: `closedloop.ControllerModel`.*

**Region.** Region 3 (pitch PI) if the trim generator speed ≥ 0.98·PC_RefSpd, otherwise Region 2
(torque control); a note is added when torque is at VS_RtTq below rated speed. Overridable per point.

**Blocks** (s = Laplace variable, ω_c corner frequency):

| Block | Transfer function | DISCON |
|---|---|---|
| Gen-speed low-pass | ω_c/(s + ω_c) or ω_c²/(s² + 2ζω_c s + ω_c²) | F_LPFType, F_LPFCornerFreq, F_LPFDamping |
| Notch | (s² + 2β_num ω s + ω²)/(s² + 2β_den ω s + ω²) | F_NotchFreqs, F_NotchBetaNum/Den, F_GenSpdNotch_*, F_TwrTopNotch_* |
| Pitch PI | β = −(K_p + K_i/s)·ω_g  (ROSCO gains are negative) | PC_GS_KP/KI scheduled on trim pitch (PC_GS_angles) |
| Floating feedback | LPF2 → HPF s/(s+ω_h) → tower-top notches → 1/s → × (−K_p,float) | Fl_Mode, F_FlCornerFreq, F_FlHighPassFreq, Fl_Kp (vs Fl_U) |
| Pitch actuator | ω/(s+ω) or ω²/(s² + 2ζωs + ω²) | PA_Mode, PA_CornerFreq, PA_Damping |
| Torque, Region 2 | τ = −(VS_Kp + VS_Ki/s)·ω_g, or 2K_{vs}ω_g (VS_ControlMode 1) | VS_KP, VS_KI, VS_Rgn2K |
| Torque, Region 3 | constant, or −P/ω_g² feedback if VS_ConstPower = 1 | VS_RtPwr, VS_GenEff |

Measurements: GenSpeed (rpm → rad/s); floating feedback uses NcIMURAys (Fl_Mode 2; else platform
pitch acceleration from the state equation) or tower-top fore-aft acceleration (Fl_Mode 1).
**Not modelled:** saturations and rate limits, set-point smoothing, peak shaving, wind-speed
estimator, filter discretisation. The time step is represented only as a pure loop delay (§8.2).

## 8. Closed loop, margins, Nyquist

The loop is broken at the main actuator (pitch in Region 3, torque in Region 2), with any secondary
feedback (constant-power torque) kept closed:

```
L(s) = −K(s) G(s)      closed loop: 1 + L = 0,    S = 1/(1+L),   T = L/(1+L)
```

| Quantity | Definition (all crossings root-found with Brent's method on the exact L(jω)) |
|---|---|
| Gain margin | at ∠L = −180° (Im L = 0, Re L < 0): GM = −1/Re L, in dB; smallest |GM| reported, all listed |
| Phase margin | at |L| = 1: PM = 180° + ∠L; smallest reported |
| Delay margin | min over crossovers of PM[rad]/ω_c |
| Stability margin | s_m = min_ω |1 + L(jω)| = 1/‖S‖∞ |
| Bandwidth of T | first −3 dB point of |T| |

### 8.1 Disk margin (gain and phase together)

Gain and phase margins each vary one thing. The symmetric disk margin allows both at once: the
loop gain is multiplied by any complex factor f in the disk

```
f = (1 + α δ/2)/(1 − α δ/2),  |δ| < 1          α_max = 1 / max_ω |S(jω) − ½|
gain within [1/g, g],  g = (2 + α)/(2 − α)      and phase within ± 2·atan(α/2)   — simultaneously
```

It is never larger than the classical margins. A common target is ±6 dB and ±37° (α = 2/3).
Reported only for a stable loop. *Code: `closedloop.disk_margin`.*

### 8.2 Loop delay (the controller as it runs)

ROSCO is called once per time step DT and its output is held: about half a step of hold plus one
step for the call. LinBench represents this by a pure delay in the loop,

```
L_τ(s) = L(s)·e^{−sτ},     default τ = 1.5·DT  (DT from the .fst; editable)
```

Gain crossovers do not move and each phase margin drops by exactly ω_c·τ; phase crossovers (gain
margin), s_m and the disk margin are recomputed on L_τ. A delayed loop has no finite A matrix, so
stability comes from the Nyquist count Z = N + P on L_τ. Filter discretisation (frequency warping)
is not modelled. *Code: `closedloop.loop_analysis(tau=…)`, `Session.delay_margins`.*

### 8.3 Pitch and torque loops together

Around rated both PI controllers act on the same filtered generator speed. Region `both` closes the
torque PI as a secondary loop and breaks the loop at pitch (`both_tq`: the reverse), giving the
margins of each loop *with the other one closed*, next to each loop alone. Reported per wind speed:

* stability with both closed (eigenvalues; independent of where the loop is broken);
* change of each loop's GM/PM when the other loop is closed;
* coupling at the pitch crossover: −20·log₁₀|1 + L_torque(jω_c)| dB, the change of the gain the pitch
  loop sees once the torque loop is closed.

Two integrators regulating one measurement leave **one closed-loop pole at the origin**: their
difference can drift without affecting the speed. In ROSCO the set-point smoother shifts one
set-point so that only one loop holds the speed; it is not part of this linear model, so the pole
at 0 is expected and is not counted as unstable. *Code: `ControllerModel.build`, `Session.interaction`.*

### 8.4 Closed-loop Campbell diagram

The closed-loop A (plant + controller states) is formed at every wind speed. A mode whose
participation is mostly in plant states inherits label, family and symmetry from the open-loop mode
with the most similar structural shape (MAC minus 0.5·|ln f ratio|); a mode mostly in controller
states is named after its block (family *controller*). Tracking across wind speeds uses the same
MAC + assignment as §4 on the plant structural part of the eigenvector. With the gains scaled to
zero the plant modes coincide with the open-loop modes (validated to 3e-13).
*Code: `closedloop.cl_modes`, `modal.track_modes`.*

**Stability** is decided from the eigenvalues of the closed-loop A (Re λ > 1e-7 ⇒ unstable) and
cross-checked with the Nyquist criterion Z = N + P on the full contour (±jω, indented to the right
around poles on the imaginary axis); N = clockwise encirclements of −1. **Root locus:** closed-loop
poles for the whole loop gain × k, k ∈ [0, 3]. **Pole labels:** participation factors over plant DOFs
and controller blocks. *Code: `closedloop.loop_analysis`, `nyquist_winding`, `root_locus`, `label_cl_poles`.*

## 9. Gain study

The controller is rebuilt with gains × factor and closed around the same linearisation.
*Code: `gainstudy.py`.*

* **Kp × Ki map:** factors on the active PI gains (pitch or torque); 1 × 1 = DISCON. Or PI (both) × Kp_float.
* **Stable:** max Re λ(A_cl) ≤ 1e-7.
* **Speed-loop pole:** the closed-loop pole below 0.5 Hz with the largest participation of the
  rotor-speed state and the PI integrator; its ω_n, ζ are what ROSCO places with ω_pc, ζ_pc.
* **Least-damped moved pole:** min ζ over closed-loop poles below 2 Hz that differ (relative
  1e-7) from every open-loop pole. Poles equal to open-loop poles cannot be moved by the gains
  (uncontrollable from the actuator or unobservable in the measurements, cf. §6); they are listed
  separately but still count for stability.
* **PI scaling across wind speeds:** both PI gains × k, k ∈ [0.25, 4]; the stable interval is found on a
  log grid (0.05…20) and refined by 30 bisection steps. Without floating feedback the upper limit
  equals the gain margin as a factor (validated to 4e-7). Gain headroom = 20 log₁₀ k_max.

## 10. ROSCO design model (1-DOF)

Rotor-speed dynamics on the generator side, aerodynamic torque τ_a = ½ρπR³U²C_p(λ, β)/λ:

```
ω̇_g = A ω_g + B_β β + B_τ τ_g
∂τ/∂β = (N_g/2) ρ π R³ U² (1/λ) ∂C_p/∂β,     ∂τ/∂λ = (N_g/2) ρ π R³ U² (λ ∂C_p/∂λ − C_p)/λ²
A = (∂τ/∂λ) · R/(U N_g) / J,     B_β = (∂τ/∂β) / J,     B_τ = −N_g²/J
```

(+ P_rated/(J ω_rated²) on A above rated when VS_ConstPower = 1; J = the drivetrain inertia from
ROSCO's turbine parameters, exactly as `rosco.toolbox` uses it). ROSCO fits A and B_β
linearly in U above rated and places the PI poles:

```
K_p = (2 ζ ω_n + A)/B,    K_i = ω_n²/B          implied by given gains:  ω_n = √(B K_i),  ζ = (B K_p − A)/(2ω_n)
```

LinBench compares three A/B sources: the linearisation (rotor-speed mode and its residue), the Cp
table at the trim point, and ROSCO's tuning schedule; and three ω_n/ζ: target (YAML), implied by the
DISCON gains on the 1-DOF model, and achieved by the full closed loop. *Code: `tuning.py`, `Session._design_row`.*

## 11. Validity and limits

* Linear, small perturbations about each trim; valid only while nothing saturates.
* Azimuth averaging assumes near-isotropic rotor behaviour; check the periodicity index after MBC.
* Near rated (Region 2½) both loops may be active or saturated; treat those points with care.
* The linearisation may already contain a ServoDyn torque law (VSContrl); LinBench flags points where
  the rotor-speed pole is much faster than the Cp-table value, because closing ROSCO's torque loop
  on top of it double-counts it.

## 12. References

* G. Bir, *Multi-blade coordinate transformation and its application to wind turbine analysis*, AIAA 2008-1300.
* ROSCO (NREL): controller, tuning and linear-model toolbox, github.com/NREL/ROSCO.
* A. M. A. Hamdan, A. H. Nayfeh, *Measures of modal controllability and observability*, J. Guidance 12(3), 1989.
* P. Van Dooren, *The generalized eigenstructure problem in linear system theory*, IEEE TAC 26, 1981.
* S. Skogestad, I. Postlethwaite, *Multivariable Feedback Control* (margins, S/T, s_m, Hankel norms).
