"""Write a ROSCO-style DISCON.IN for the synthetic rotor so the closed-loop screens can be tried
without a ROSCO checkout.   python tests/make_synthetic_discon.py

Gains follow ROSCO's pole-placement rule on the 1-DOF rotor model  w' = A w + B u  identified from
each synthetic linearisation (A, B = rotor-speed mode and its residue):
    Kp = (2 zeta wn + A) / B,   Ki = wn^2 / B
pitch loop: wn = 0.25 rad/s, zeta = 0.8, scheduled on the trim pitch of the pitch-controlled points;
torque loop: wn = 0.20 rad/s, zeta = 0.7 at the lowest wind speed.  Synthetic data, not a real turbine.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
sys.path.insert(0, ROOT)
from linbench.analysis import Session  # noqa: E402

FOLDER = os.path.join(ROOT, 'samples', 'synthetic_rotor')
OUT = os.path.join(FOLDER, 'synthetic_DISCON.IN')
WN_PC, Z_PC, WN_VS, Z_VS = 0.25, 0.8, 0.20, 0.7


def main():
    s = Session()
    s._load(dict(lin_dir=FOLDER, mode='open'))
    if s.progress.get('error'):
        raise SystemExit(s.progress['error'])
    ops, design = s.summary['ops'], s.summary['design']
    rated = max(o['rotor_rpm'] for o in ops) * 3.141592653589793 / 30          # gearbox ratio 1
    pitch_pts = [(o, d) for o, d in zip(ops, design) if o['rotor_rpm'] >= 0.98 * max(x['rotor_rpm'] for x in ops)]
    angles, kp, ki = [], [], []
    for o, d in pitch_pts:
        A, B = d['A_lin'], d['B_lin']
        angles.append(o['pitch_deg'] * 3.141592653589793 / 180)
        kp.append((2 * Z_PC * WN_PC + A) / B)
        ki.append(WN_PC ** 2 / B)
    # torque loop: w' = A w + Bt tau with Bt = -1 / J_total (rotor + blades, see make_synthetic.P)
    om = s.models[0]
    A0 = design[0]['A_lin']
    Bt = -1.0 / (4.0e7 + 3 * 5000.0 * 20.0 ** 2)
    vs_kp, vs_ki = (2 * Z_VS * WN_VS + A0) / Bt, WN_VS ** 2 / Bt
    tq = ops[0]['gen_torque']
    fmt = lambda v: ' '.join(f'{x:.6g}' for x in v)
    lines = [
        '! Controller parameter input file for the synthetic rotor (LinBench sample; NOT a real turbine)',
        f'!   written by tests/make_synthetic_discon.py: pitch wn={WN_PC} zeta={Z_PC}, torque wn={WN_VS} zeta={Z_VS}',
        '',
        '!------- CONTROLLER FLAGS -------------------------------------------------',
        '1                   ! F_LPFType         - 1 = first-order, 2 = second-order low-pass filter',
        '2                   ! VS_ControlMode    - 2 = TSR tracking PI torque control',
        '0                   ! VS_ConstPower     - 0 = constant torque above rated',
        '1                   ! PC_ControlMode    - 1 = PI pitch control',
        '0                   ! PS_Mode           - no peak shaving',
        '0                   ! Fl_Mode           - no floating feedback',
        '2                   ! PA_Mode           - second-order pitch actuator',
        '0                   ! WE_Mode           - no wind-speed estimator',
        '',
        '!------- FILTERS ----------------------------------------------------------',
        '1.0                 ! F_LPFCornerFreq   - generator-speed low-pass corner [rad/s]',
        '0.7                 ! F_LPFDamping      - (only for F_LPFType = 2)',
        '0                   ! F_NumNotchFilts   - number of notch filters',
        '',
        '!------- BLADE PITCH CONTROL ----------------------------------------------',
        f'{len(angles)}                   ! PC_GS_n           - gain-schedule points',
        f'{fmt(angles)}   ! PC_GS_angles      - blade pitch [rad]',
        f'{fmt(kp)}   ! PC_GS_KP          - proportional gain [s]',
        f'{fmt(ki)}   ! PC_GS_KI          - integral gain [-]',
        f'{rated:.6g}          ! PC_RefSpd         - rated generator speed [rad/s]',
        '0.0                 ! PC_MinPit         - minimum pitch [rad]',
        '',
        '!------- GENERATOR TORQUE CONTROL -----------------------------------------',
        f'{rated * tq * 1.1:.6g}          ! VS_RtPwr          - rated power [W]',
        f'{tq * 1.1:.6g}          ! VS_RtTq           - rated torque [N m]',
        '100.0               ! VS_GenEff         - generator efficiency [%]',
        f'{vs_kp:.6g}        ! VS_KP             - torque PI proportional gain [N m s/rad]',
        f'{vs_ki:.6g}        ! VS_KI             - torque PI integral gain [N m/rad]',
        '',
        '!------- PITCH ACTUATOR ---------------------------------------------------',
        '1.5708              ! PA_CornerFreq     - actuator natural frequency [rad/s]',
        '0.707               ! PA_Damping        - actuator damping ratio',
        '',
        '!------- DRIVETRAIN -------------------------------------------------------',
        '1.0                 ! WE_GearboxRatio   - gearbox ratio',
        '',
    ]
    with open(OUT, 'w') as fh:
        fh.write('\n'.join(lines))
    print('wrote', OUT)
    for a, p, i in zip(angles, kp, ki):
        print(f'  pitch {a * 57.2958:6.2f} deg   Kp {p:9.4f} s   Ki {i:9.4f}')
    print(f'  torque loop VS_KP {vs_kp:.4g}  VS_KI {vs_ki:.4g}')


if __name__ == '__main__':
    main()
