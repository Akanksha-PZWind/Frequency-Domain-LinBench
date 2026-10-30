"""Where the reference data lives.  Set ROSCO_DIR to a ROSCO repository checkout
(github.com/NREL/ROSCO) to run the checks that use its IEA-15MW example and its own MBC code."""
import os

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)


def rosco_dir():
    for c in (os.environ.get('ROSCO_DIR'), os.path.join(ROOT, '..', 'rosco'), os.path.join(ROOT, '..', 'ROSCO')):
        if c and os.path.isdir(os.path.join(c, 'Examples')):
            return os.path.abspath(c)
    return None


R = rosco_dir()
CASE = os.path.join(R, 'Examples/Test_Cases/IEA-15-240-RWT/IEA-15-240-RWT-UMaineSemi') if R else None
IEA = os.path.join(CASE, 'linearizations') if CASE else None
DISCON = os.path.join(CASE, 'IEA-15-240-RWT-UMaineSemi_DISCON.IN') if CASE else None
TUNE = os.path.join(R, 'Examples', 'Tune_Cases') if R else None
SYN = os.path.join(ROOT, 'samples', 'synthetic_rotor')
TRUTH = os.path.join(HERE, 'data', 'rosco_truth.json')
