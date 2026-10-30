# LinBench validation

Run 2026-09-28 04:35 · Python 3.11.15 · reference: ROSCO 2.10.4

## Rotor-mode symmetry and whirl (synthetic isotropic rotor) — pass

| check | value |
|---|---|
| synthetic: max error of FW/BW flap eigenvalues vs lambda_rot +/- j*Omega | 1.725e-08 |
| synthetic: Campbell tracks (complete over all wind speeds) | 8.000e+00 |

## Delay, disk margin, both loops closed, closed-loop Campbell, disk cache (synthetic DISCON) — pass
Run 2026-10-07 · Python 3.14.7 (no ROSCO needed)

| check | value |
|---|---|
| delay: max ‖PM(tau) - (PM - w_c tau)‖ at gain crossovers [deg] | 2.487e-14 |
| delay: points where stability flips at the analytic delay margin (+-5 %) | 8.000e+00 |
| disk margin: rel error of alpha vs dense-grid 1/max‖S - 1/2‖ | 1.032e-05 |
| disk margin: gain perturbations at 98 % of the disk limit, all stable | 1.600e+01 |
| both loops: closed-loop poles independent of where the loop is broken (max diff) | 2.842e-14 |
| both loops: poles at the origin (two integrators on one measurement) | 1.000e+00 |
| closed-loop modes at ~zero gain vs open-loop modes (frequency + damping, labels equal) | 3.315e-13 |
| closed-loop Campbell: structural tracks complete over all wind speeds | 8.000e+00 |
| disk cache: reload vs fresh, max ‖A diff‖ + ‖B diff‖ | 0.000e+00 |

## Grouping when rotor speed differs between azimuths (drift / ripple / scatter / two sets) — pass
Run 2026-09-30 · Python 3.14.7 (no ROSCO needed)

| check | value |
|---|---|
| drift 5 %: azimuth files kept in one operating point | 8.000e+00 |
| drift 5 %: adjusted R^2 time / azimuth | 1.000e+00 |
| drift 5 %: max mode-frequency spread between azimuth models [%] | 9.356e-01 |
| clean set: max mode-frequency spread between azimuth models [%] | 3.209e-07 |
| periodic +-0.4 % ripple: classified periodic, notice only | 1.000e+00 |
| random 1 % scatter: classified scatter, warning | 1.000e+00 |
| two full sets at 17 m/s (11.0 / 12.1 rpm): separate points | 2.000e+00 |
| reference synthetic set: 8 points x 8 azimuths, no rotor-speed warning | 1.000e+00 |

## Controllability, observability, Kalman decomposition (known structures) — pass
Run 2026-09-28 · Python 3.14.7 (no ROSCO needed)

| check | value |
|---|---|
| random Kalman structures (20): ranks and minimal order recovered | 1.000e+00 |
| random Kalman structures: max rel error of uncontrollable/unobservable eigenvalues | 1.177e-10 |
| stable/unstable split: HSV rel error vs known stable part | 4.077e-14 |

## Hankel singular values and PBH tests (synthetic rotor) — pass
Run 2026-09-28 · Python 3.14.7 (no ROSCO needed)

| check | value |
|---|---|
| synthetic: HSV rel error vs brute-force Gramians | 4.957e-14 |
| synthetic: ‖significant HSVs - minimal order‖ | 0.000e+00 |
| synthetic: collective pitch leaves exactly the whirl (+ tower SS) modes uncontrollable | 1.000e+00 |

## Browser-side curves: pole/residue exports, open loop (synthetic rotor) — pass
Run 2026-09-29 · Python 3.14.7 (no ROSCO needed)

| check | value |
|---|---|
| open loop: pairs recognised as not connected (G = 0) | 1.600e+01 |
| open loop: cases exported (synthetic, all ops x standard pairs) | 5.600e+01 |
| open loop: max ‖G_modal - G_ss‖ / max‖G‖ over 1e-6..1e4 rad/s | 1.208e-13 |
| open loop: step/impulse max rel error out to 3x duration | 8.027e-14 |
| open loop: cases with pole-zero phase | 5.600e+01 |
| open loop: phase vs numpy unwrap, spread after removing whole turns [rad] | 1.295e-10 |

## Browser-side curves: loop and controller exports (synthetic rotor + synthetic DISCON) — pass
Run 2026-09-29 · Python 3.14.7 (no ROSCO needed)

| check | value |
|---|---|
| closed loop: operating points with an exported L / K | 8.000e+00 |
| closed loop: max rel error of exported L(jw) vs loop_eval | 2.911e-11 |
| closed loop: max rel error of exported K(jw) vs ctrl_eval | 5.659e-13 |

## Browser-side curves: loop and controller exports (IEA-15MW) — not yet run
needs ROSCO_DIR; run python tests/run_all.py with a ROSCO checkout

## Gain study: stability map, PI-scaling limits, speed-loop pole (synthetic DISCON) — pass
Run 2026-09-29 · Python 3.14.7 (no ROSCO needed)

| check | value |
|---|---|
| factors 1 x 1 vs DISCON closed loop: max ‖A diff‖ | 0.000e+00 |
| upper PI-scaling limit vs gain margin, max rel diff | 4.073e-07 |
| random gain settings: map verdict = eigenvalue/Nyquist verdict | 1.000e+00 |
| random gain settings checked (all with Z = N + P) | 4.800e+01 |
| speed-loop pole vs design comparison, max rel diff | 0.000e+00 |

## MBC transform vs ROSCO fx_mbc3, periodicity, file-name invariance — pass

| check | value |
|---|---|
| IEA15-semi U=5 eig rel err vs ROSCO fx_mbc3 | 8.035e-14 |
| IEA15-semi U=15 eig rel err vs ROSCO fx_mbc3 | 2.210e-14 |
| IEA15-semi U=24 eig rel err vs ROSCO fx_mbc3 | 2.755e-14 |
| synthetic U=6 eig rel err vs ROSCO fx_mbc3 | 7.734e-15 |
| synthetic U=14 eig rel err vs ROSCO fx_mbc3 | 4.900e-15 |
| synthetic U=24 eig rel err vs ROSCO fx_mbc3 | 9.285e-15 |
| synthetic max periodicity after MBC | 2.886e-07 |
| synthetic periodicity before MBC | 5.014e-02 |
| IEA collective(B pitch 1..3) vs extended input, rel diff | 2.920e-05 |
| renamed/shuffled set: max ‖A diff‖ | 0.000e+00 |

## Transfer functions (modal residues, DC gain, zeros) — pass

| check | value |
|---|---|
| IEA 17 m/s: modal-residue vs direct frequency response, max rel diff | 1.092e-12 |
| IEA 17 m/s: DC gain vs direct G(j*1e-9), rel diff | 4.709e-15 |
| IEA 17 m/s: max ‖G(z)‖ over reported zeros (scaled) | 1.574e-10 |

## ROSCO controller model, loop gain, closed-loop poles, Nyquist — pass

| check | value |
|---|---|
| IEA all ops: controller K(jw) vs analytic ROSCO filter/PI chain, max rel diff | 3.084e-14 |
| IEA all ops: vectorised L(jw) vs state-space loop, max rel diff | 7.903e-10 |
| IEA all ops: max ‖1+L(lambda)‖ / max(1,‖L‖) at every closed-loop pole that moved | 8.388e-11 |
| IEA all ops: Nyquist Z = N + P holds | 1.000e+00 |

## Tuning re-implementation vs ROSCO tune_controller — pass

| check | value |
|---|---|
| IEA15MW_robust.yaml:v | 0.000e+00 |
| IEA15MW_robust.yaml:A | 0.000e+00 |
| IEA15MW_robust.yaml:B_beta | 0.000e+00 |
| IEA15MW_robust.yaml:pitch_op | 0.000e+00 |
| IEA15MW_robust.yaml:pc_Kp | 0.000e+00 |
| IEA15MW_robust.yaml:pc_Ki | 0.000e+00 |
| IEA15MW_robust.yaml:PC_GS_angles | 0.000e+00 |
| IEA15MW_robust.yaml:vs_Kp | 0.000e+00 |
| IEA15MW_robust.yaml:vs_Ki | 0.000e+00 |
| IEA15MW.yaml:v | 0.000e+00 |
| IEA15MW.yaml:A | 0.000e+00 |
| IEA15MW.yaml:B_beta | 0.000e+00 |
| IEA15MW.yaml:pitch_op | 0.000e+00 |
| IEA15MW.yaml:pc_Kp | 0.000e+00 |
| IEA15MW.yaml:pc_Ki | 0.000e+00 |
| IEA15MW.yaml:PC_GS_angles | 0.000e+00 |
| IEA15MW.yaml:vs_Kp | 0.000e+00 |
| IEA15MW.yaml:vs_Ki | 0.000e+00 |
