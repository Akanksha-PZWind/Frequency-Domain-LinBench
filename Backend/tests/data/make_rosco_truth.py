"""Ground truth: run ROSCO's own Controller.tune_controller on a manually built Turbine.
Writes tests/data/rosco_truth.json.  Needs a ROSCO checkout: set ROSCO_DIR."""
import sys, types, os, json
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from _paths import R as ROSCO_DIR
sys.path.insert(0, ROSCO_DIR)
# openfast_io is only needed by Turbine.load_from_fast; stub it so turbine.py imports
if 'openfast_io' not in sys.modules:
    m = types.ModuleType('openfast_io'); m.FileTools = types.SimpleNamespace()
    sys.modules['openfast_io'] = m
from rosco.toolbox import controller as ROC
from rosco.toolbox import turbine as ROT
from rosco.toolbox.utilities import load_from_txt
import yaml, copy
def _fill(inst, schema):
    """Fill defaults from a JSON-schema-like dict, recursively (what ROSCO's _validate does)."""
    if schema.get('type') == 'object' or 'properties' in schema:
        if inst is None:
            inst = copy.deepcopy(schema.get('default', {}))
        for k, sub in schema.get('properties', {}).items():
            if k not in inst:
                if sub.get('type') == 'object' or 'properties' in sub:
                    inst[k] = _fill(copy.deepcopy(sub.get('default', {})) or {}, sub)
                elif 'default' in sub:
                    inst[k] = copy.deepcopy(sub['default'])
            else:
                if isinstance(inst[k], dict):
                    inst[k] = _fill(inst[k], sub)
    return inst
def load_rosco_yaml(path):
    sch = yaml.safe_load(open(os.path.join(ROSCO_DIR, "rosco/toolbox/inputs/toolbox_schema.yaml")))
    return _fill(yaml.safe_load(open(path)), sch)

def truth(yaml_path, fast):
    inps = load_rosco_yaml(yaml_path)
    tp, cp = inps['turbine_params'], inps['controller_params']
    tb = ROT.Turbine(tp)
    tb.TipRad = fast['TipRad']; tb.rotor_radius = fast['TipRad']
    tb.Rhub = fast['HubRad']; tb.rho = fast['AirDens']; tb.Ng = fast['GBRatio']
    tb.GenEff = fast['GenEff']; tb.GBoxEff = fast['GBoxEff']
    tb.generator_inertia = fast['GenIner']
    tb.J = tb.rotor_inertia + tb.generator_inertia * tb.Ng**2
    tb.rated_torque = tb.rated_power/(tb.GenEff/100*tb.rated_rotor_speed*tb.Ng)
    tb.NumBl = 3; tb.TowerHt = fast['TowerHt']; tb.hubHt = fast['TowerHt'] + 3.0; tb.shearExp = 0.2
    pth = os.path.join(os.path.dirname(yaml_path), inps['path_params']['rotor_performance_filename'])
    tb.pitch_initial_rad, tb.TSR_initial, tb.Cp_table, tb.Ct_table, tb.Cq_table = load_from_txt(pth)
    tb.Cp = ROT.RotorPerformance(tb.Cp_table, tb.pitch_initial_rad, tb.TSR_initial)
    tb.Ct = ROT.RotorPerformance(tb.Ct_table, tb.pitch_initial_rad, tb.TSR_initial)
    tb.Cq = ROT.RotorPerformance(tb.Cq_table, tb.pitch_initial_rad, tb.TSR_initial)
    if not tb.TSR_operational:
        tb.TSR_operational = tb.Cp.TSR_opt
    tb.Cp_operational = tb.Cp.interp_surface(tb.Cp.pitch_opt, tb.TSR_operational)
    ctrl = ROC.Controller(cp)
    ctrl.tune_controller(tb)
    out = dict(
        v=ctrl.v.tolist(), A=np.ravel(ctrl.A).tolist(), B_beta=np.ravel(ctrl.B_beta).tolist(),
        B_tau=np.ravel(ctrl.B_tau).tolist(), pitch_op=np.ravel(ctrl.pitch_op).tolist(),
        TSR_op=np.ravel(ctrl.TSR_op).tolist(),
        pc_Kp=np.ravel(ctrl.pc_gain_schedule.Kp).tolist(), pc_Ki=np.ravel(ctrl.pc_gain_schedule.Ki).tolist(),
        vs_Kp=np.ravel(ctrl.vs_gain_schedule.Kp).tolist(), vs_Ki=np.ravel(ctrl.vs_gain_schedule.Ki).tolist(),
        PC_GS_angles=np.ravel(ctrl.pitch_op_pc).tolist() if hasattr(ctrl,'pitch_op_pc') else None,
        omega_pc_U=np.ravel(ctrl.omega_pc_U).tolist(), zeta_pc_U=np.ravel(ctrl.zeta_pc_U).tolist(),
        F_LPFCornerFreq=float(getattr(ctrl,'F_LPFCornerFreq', np.nan)),
        F_LPFDamping=float(getattr(ctrl,'F_LPFDamping', np.nan)),
        Kp_float=np.ravel(getattr(ctrl,'Kp_float',[np.nan])).tolist(),
        U_Fl=np.ravel(getattr(ctrl,'U_Fl',[np.nan])).tolist(),
        F_FlCornerFreq=np.ravel(getattr(ctrl,'F_FlCornerFreq',[np.nan])).tolist(),
        F_FlHighPassFreq=float(np.ravel(getattr(ctrl,'F_FlHighPassFreq',[np.nan]))[0]),
        vs_refspd=float(ctrl.vs_refspd), VS_ConstPower=int(ctrl.VS_ConstPower),
        WS_GS_n=int(ctrl.WS_GS_n), PC_GS_n=int(ctrl.PC_GS_n), min_pitch=float(ctrl.min_pitch),
        J=float(tb.J))
    return out

if __name__ == '__main__':
    fast = dict(TipRad=120.97, HubRad=3.97, AirDens=1.225, GBRatio=1.0, GenEff=95.756,
                GBoxEff=100.0, GenIner=1836784.0, TowerHt=144.386)
    res = {}
    for name in ['IEA15MW_robust.yaml', 'IEA15MW.yaml']:
        p = os.path.join(ROSCO_DIR, "Examples/Tune_Cases", name)
        try:
            res[name] = truth(p, fast)
            print(name, 'OK  PC_GS_n', res[name]['PC_GS_n'], ' F_LPF', res[name]['F_LPFCornerFreq'],
                  ' Kp_float', res[name]['Kp_float'])
        except Exception as e:
            import traceback; traceback.print_exc()
    json.dump(res, open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "rosco_truth.json"), "w"), indent=1)
