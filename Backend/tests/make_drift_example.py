"""Write samples/rotor_speed_drift: the synthetic rotor at 14, 17 and 20 m/s where the 17 m/s
azimuth sweep was linearised while the rotor was still accelerating (rotor speed rises by 3 %
over the sweep, as with an unconverged trim).  Used to demonstrate the rotor-speed diagnostics.
    python tests/make_drift_example.py
"""
import math
import os
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import make_synthetic as MS  # noqa: E402

OUT = os.path.join(os.path.dirname(HERE), 'samples', 'rotor_speed_drift')
N, SPREAD = 8, 0.03


def main():
    if os.path.isdir(OUT):
        shutil.rmtree(OUT)
    cases = []
    for U, t0 in ((14.0, 100.0), (17.0, 300.0), (20.0, 500.0)):
        W0 = MS.rated_speed(U)
        for j in range(N):
            om = W0 * (1 + SPREAD * (j / (N - 1) - 0.5)) if U == 17.0 else W0
            cases.append((U, 2 * math.pi * j / N + 0.013, om, t0 + 0.6 * j))
    MS.write_files(OUT, cases, prefix='drift')
    print('wrote', OUT)


if __name__ == '__main__':
    main()
