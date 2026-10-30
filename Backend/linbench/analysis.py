"""Session: load a linearisation set (+ optional ROSCO inputs) and serve analysis results."""
from __future__ import annotations

import math
import os
import threading
import time
import traceback
from collections import Counter

import numpy as np
from scipy import linalg

from . import __version__
from . import closedloop as CL
from . import lti, rosco_io, tuning
from . import gainstudy as GS
from . import structure as ST
from . import cache
from .discover import find_lin_files, scan_folder
from .linfile import clear_cache, read_many
from .modal import analyse_modes, campbell, mode_table, track_modes
from .model import OpModel, unit_convert
from .tf import SISO, freq_grid, step_metrics


def clean(o):
    """JSON-safe: numpy -> python, NaN/inf -> None, complex -> [re, im]."""
    if isinstance(o, dict):
        return {str(k): clean(v) for k, v in o.items() if not str(k).startswith('_')}
    if isinstance(o, (list, tuple)):
        return [clean(v) for v in o]
    if isinstance(o, np.ndarray):
        return clean(o.tolist())
    if isinstance(o, (np.bool_, bool)):
        return bool(o)
    if isinstance(o, (np.integer,)):
        return int(o)
    if isinstance(o, (complex, np.complexfloating)):
        return [clean(float(o.real)), clean(float(o.imag))]
    if isinstance(o, (float, np.floating)):
        f = float(o)
        return f if math.isfinite(f) else None
    return o


def _thin(w, *arrs, n=900):
    """Down-sample dense curves for transport while keeping the ends."""
    if len(w) <= n:
        return (w,) + arrs
    idx = np.unique(np.round(np.linspace(0, len(w) - 1, n)).astype(int))
    return (np.asarray(w)[idx],) + tuple(np.asarray(a)[idx] for a in arrs)


class Session:
    def __init__(self):
        self.lock = threading.Lock()
        self.progress = dict(stage='idle', pct=0, msg='', done=False, error=None)
        self.models = []
        self.cfg = {}
        self.discon = None
        self.yaml = None
        self.cp = None
        self.fast = {}
        self.cms = {}
        self.region_override = {}
        self.summary = None
        self._gcache, self._ccache = {}, {}
        self._t0 = time.time()

    # ------------------------------------------------------------------ inputs
    @staticmethod
    def inspect(paths: dict):
        """Resolve what the user pointed at; fill blanks from a tuning YAML where possible."""
        out = dict(lin_dir=paths.get('lin_dir') or '', discon=paths.get('discon') or '',
                   yaml=paths.get('yaml') or '', cp=paths.get('cp') or '', fst=paths.get('fst') or '',
                   found={}, messages=[])
        y = out['yaml']
        if y:
            if not os.path.isfile(y):
                out['messages'].append(f'YAML not found: {y}')
            else:
                try:
                    d = rosco_io.discover_from_yaml(y)
                    for key, val in (('lin_dir', d['lin']), ('discon', d['discon']), ('cp', d['cp']), ('fst', d['fst'])):
                        if val and not out[key]:
                            out[key] = val
                            out['found'][key] = 'from YAML'
                except Exception as e:
                    out['messages'].append(f'YAML could not be read: {e}')
        if not out['discon'] and out['lin_dir'] and os.path.isdir(out['lin_dir']):
            # a DISCON next to the linearisations (or one folder up) is picked up automatically
            for base in (out['lin_dir'], os.path.dirname(os.path.abspath(out['lin_dir']))):
                try:
                    ds = [f for f in os.listdir(base) if 'discon' in f.lower() and f.lower().endswith('.in')]
                except OSError:
                    ds = []
                if len(ds) == 1:
                    out['discon'] = os.path.join(base, ds[0])
                    out['found']['discon'] = 'in the linearisation folder' if base == out['lin_dir'] else 'next to the linearisation folder'
                    break
        if out['discon'] and os.path.isfile(out['discon']) and not out['cp']:
            try:
                dis = rosco_io.read_discon(out['discon'])
                pf = dis.get('PerfFileName')
                p = rosco_io.resolve(pf, os.path.dirname(out['discon'])) if isinstance(pf, str) else None
                if p:
                    out['cp'] = p
                    out['found']['cp'] = 'from DISCON PerfFileName'
            except Exception as e:
                out['messages'].append(f'DISCON could not be read: {e}')
        if out['discon'] and not out['fst']:
            base = os.path.dirname(out['discon'])
            fs = [f for f in os.listdir(base) if f.lower().endswith('.fst')] if os.path.isdir(base) else []
            if len(fs) == 1:
                out['fst'] = os.path.join(base, fs[0])
                out['found']['fst'] = 'next to DISCON'
        if out['discon'] and not out['yaml']:
            base = os.path.dirname(out['discon'])
            ys = [f for f in os.listdir(base) if f.lower().endswith(('.yaml', '.yml'))] if os.path.isdir(base) else []
            for f in sorted(ys, key=lambda f: ('rosco' not in f.lower(), f)):
                try:
                    d = rosco_io.read_yaml(os.path.join(base, f))
                    if 'controller_params' in d and 'turbine_params' in d:
                        out['yaml'] = os.path.join(base, f)
                        out['found']['yaml'] = 'next to DISCON'
                        break
                except Exception:
                    continue
        if out['lin_dir'] and os.path.isdir(out['lin_dir']):
            try:
                out['scan'] = scan_folder(out['lin_dir'], bool(paths.get('recursive')), use_cache=True)[0]
            except Exception as e:
                out['messages'].append(f'Folder scan failed: {e}')
        return out

    # ------------------------------------------------------------------ loading
    def _set(self, stage, pct, msg=''):
        self.progress.update(stage=stage, pct=pct, msg=msg, elapsed=time.time() - self._t0)

    def _log(self, stage, text, **data):
        """One line of the live analysis log shown while loading (data feeds the live charts)."""
        self.progress['log'].append(dict(t=round(time.time() - self._t0, 2), stage=stage, text=text, data=data))

    def start_load(self, cfg):
        self._t0 = time.time()
        self.progress = dict(stage='starting', pct=0, msg='', done=False, error=None, log=[], elapsed=0.0)
        t = threading.Thread(target=self._load, args=(cfg,), daemon=True)
        t.start()

    def _load(self, cfg):
        try:
            t0 = time.time()
            if 'log' not in self.progress:
                self._t0 = t0
                self.progress.update(log=[], elapsed=0.0)
            self.cfg = cfg
            self.cms, self.region_override, self.summary = {}, {}, None
            self._gcache, self._ccache = {}, {}
            self._set('scan', 2, 'Scanning folder')
            rm_az = cfg.get('remove_azimuth', True)
            files = find_lin_files(cfg['lin_dir'], cfg.get('recursive', False)) if os.path.isdir(cfg['lin_dir']) else []
            ckey = cache.key('models', files, cfg.get('recursive', False), rm_az)
            hit = cache.load(ckey) if files and cfg.get('cache') else None
            if hit is not None:
                info, groups, models, camp = hit
            else:
                info, groups = scan_folder(cfg['lin_dir'], cfg.get('recursive', False), use_cache=bool(cfg.get('cache')))
                models, camp = None, None
            if not groups:
                raise ValueError('No OpenFAST .lin files found in the selected folder.')
            self.scan = info
            self._log('scan', f"{info.get('n_files', sum(len(g.files) for g in groups))} .lin files → {len(groups)} operating points "
                      f"({groups[0].wind_speed:g}–{groups[-1].wind_speed:g} m/s), grouped by the wind speed "
                      "written inside each file and ordered by azimuth"
                      + (' · processed models taken from the cache (no file changed since the last run)' if hit is not None else ''),
                      n_ops=len(groups), n_files=sum(len(g.files) for g in groups), cached=hit is not None)
            fresh = models is None
            if fresh:
                self._set('read', 4, f'Reading {sum(len(g.files) for g in groups)} files')
                read_many([f for g in groups for f in g.files], full=True)      # parallel; OpModel then hits the cache
                models = []
            for i, g in enumerate(groups):
                if fresh:
                    self._set('read', 8 + int(52 * i / len(groups)),
                              f'MBC transform and averaging at {g.wind_speed:g} m/s ({len(g.files)} files)')
                    om = OpModel(g.files, i, remove_azimuth=rm_az)
                    om.scan_warnings = g.warnings
                    om.omega_check = g.omega_check
                    models.append(om)
                om = models[i]
                lam = linalg.eigvals(om.A)
                mag = np.abs(lam)
                osc = (lam.imag > 1e-9) & (mag > 1e-9)
                zmin = float(np.min(-lam.real[osc] / mag[osc])) if osc.any() else None
                if i == 0:
                    self._log('read', f'Model size: {om.A.shape[0]} states, {om.B.shape[1]} inputs, {om.C.shape[0]} outputs · '
                              f'{len(om.plan.x_pos2) + len(om.plan.x_first)} rotor state triplets and '
                              f'{len(om.plan.u_trip)} input triplets go through the Coleman (MBC) transform'
                              + (f' · removed: {", ".join(s.split(",")[0] for s in om.removed_states)}' if om.removed_states else ''))
                self._log('read', f'{om.wind:g} m/s · {len(g.files)} azimuths · Ω = {om.omega * 30 / math.pi:.3g} rpm · '
                          f'periodicity {om.periodicity_raw * 100:.3g}% → {om.periodicity_mbc * 100:.3g}% after MBC · '
                          f'max Re λ = {lam.real.max():.3g} 1/s' + (f' · least damping ζ = {zmin * 100:.3g}%' if zmin is not None else ''),
                          wind=om.wind, per_raw=om.periodicity_raw, per_mbc=om.periodicity_mbc,
                          max_re=float(lam.real.max()), zmin=zmin, n_az=len(g.files),
                          spread=g.omega_check.get('spread', 0.0))
                oc = g.omega_check
                if oc.get('level') in ('warning', 'notice'):
                    self._log('read', f"{'WARNING' if oc['level'] == 'warning' else 'note'} {om.wind:g} m/s · "
                              f"Ω {min(g.speeds) * 30 / math.pi:.4g}–{max(g.speeds) * 30 / math.pi:.4g} rpm across azimuths "
                              f"({oc['kind']}) · {oc['message']}", wind=om.wind, level=oc['level'])
            clear_cache()                                   # parsed files are no longer needed
            self.models = models
            self.discon = rosco_io.read_discon(cfg['discon']) if cfg.get('discon') else None
            self.yaml = rosco_io.read_yaml(cfg['yaml']) if cfg.get('yaml') else None
            self.cp = None
            if cfg.get('cp'):
                try:
                    self.cp = rosco_io.read_cp_table(cfg['cp'])
                except Exception as e:
                    self.progress['msg'] = f'Cp table not usable: {e}'
            self.fast = rosco_io.read_fast_params(cfg.get('fst')) if cfg.get('fst') else {}
            self.dt = None                                  # controller / simulation time step, if the .fst is known
            try:
                dt = rosco_io.read_openfast_keys(cfg['fst']).get('DT') if cfg.get('fst') and os.path.isfile(cfg['fst']) else None
                self.dt = float(dt) if isinstance(dt, (int, float)) and dt > 0 else None
            except Exception:
                pass
            if self.discon:
                self._log('read', f"DISCON read: {len(self.discon) - 1} parameters · Fl_Mode {self.discon.get('Fl_Mode', 0)} · "
                          f"PA_Mode {self.discon.get('PA_Mode', 0)} · VS_ConstPower {self.discon.get('VS_ConstPower', 0)}")
            self._set('modes', 62, 'Eigen-analysis and mode identification')
            for i, om in enumerate(models):
                self._set('modes', 62 + int(9 * i / len(models)), f'Eigen-analysis at {om.wind:g} m/s')
                ms = analyse_modes(om)
                osc = [m for m in ms if m['oscillatory'] and m['family'] != 'hydro/aero states']
                lz = min(osc, key=lambda m: m['zeta']) if osc else None
                self._log('modes', f'{om.wind:g} m/s · {len(ms)} modes, {len(osc)} oscillatory structural'
                          + (f' · least damped: {lz["label"]} {lz["fn"]:.3g} Hz, ζ = {lz["zeta"] * 100:.3g}%' if lz else ''),
                          wind=om.wind, zmin=lz['zeta'] if lz else None, label=lz['label'] if lz else None,
                          modes=[[m['fn'], m['zeta']] for m in osc if m['fn'] <= 3])
            self._set('campbell', 72, 'Tracking modes across wind speeds (MAC)')
            self.campbell = camp if camp is not None else campbell(models)
            self.campbell_all = None
            self.campbell_cl = None
            self._log('campbell', f"{len(self.campbell['tracks'])} mode tracks joined across {len(models)} wind speeds "
                      "by mode-shape similarity (MAC) and optimal assignment")
            if fresh and cfg.get('cache'):
                cache.save(ckey, (info, groups, models, self.campbell))
            mode = cfg.get('mode', 'both')
            self.cl_summary = []
            closing = bool(self.discon and mode in ('closed', 'both'))
            # open-loop results are complete here: publish them, the closed loop follows in this thread
            self.summary = self._build_summary(pending=closing)
            self.summary['load_time_s'] = time.time() - t0
            self.progress['open_done'] = True
            self._log('campbell', f'Open-loop results ready after {time.time() - t0:.2f} s'
                      + (' · closing the loop next, selected wind speed first' if closing else ''))
            if closing:
                first = next((k for k, m in enumerate(models) if m.wind >= 17 - 1e-6), len(models) // 2)
                order = [first] + [k for k in range(len(models)) if k != first]
                rows = {}
                for n_done, i in enumerate(order):
                    om = models[i]
                    self._set('closed', 75 + int(22 * n_done / len(models)), f'Closing the loop at {om.wind:g} m/s')
                    row = self._cl_row(i)
                    rows[i] = row
                    self.cl_summary = [rows[k] for k in sorted(rows)]
                    if row.get('error'):
                        self._log('closed', f"{om.wind:g} m/s · {row['error']}", wind=om.wind)
                        continue
                    gains = (f"Kp {row['Kp']:.4g} s, Ki {row['Ki']:.4g}" if row['region'] == 'pitch'
                             else f"VS_Kp {row['VS_Kp']:.4g}, VS_Ki {row['VS_Ki']:.4g}")
                    self._log('closed', f"{om.wind:g} m/s · {row['region_name']} · {gains} · "
                              f"{'STABLE' if row['stable'] else 'UNSTABLE'} (Z = {row['Z']}, P = {row['P']}, N = {row['N']})"
                              + (f" · GM {row['gm_db']:.3g} dB" if row['gm_db'] is not None else '')
                              + (f" · PM {row['pm']:.3g}°" if row['pm'] is not None else '')
                              + f" · s_m {row['s_m']:.3g}",
                              wind=om.wind, stable=row['stable'], gm=row['gm_db'], pm=row['pm'], sm=row['s_m'])
            self._set('summary', 98, 'Summarising')
            self.summary = self._build_summary()
            self.summary['load_time_s'] = time.time() - t0
            self._log('summary', f'Done in {time.time() - t0:.1f} s')
            self._set('done', 100, 'Ready')
            self.progress['done'] = True
        except Exception as e:
            tb = traceback.format_exc()
            print(tb, flush=True)
            self.progress.update(error=f'{type(e).__name__}: {e}', done=True, trace=tb,
                                 failed_step=self.progress.get('msg') or self.progress.get('stage'))

    # ------------------------------------------------------------------ closed loop helpers
    def cm(self, i):
        reg = self.region_override.get(i, 'auto')
        key = (i, reg)
        if key not in self.cms:
            self.cms[key] = CL.ControllerModel(self.models[i], self.discon, region=reg)
        return self.cms[key]

    def _cl_row(self, i):
        om = self.models[i]
        try:
            cm = self.cm(i)
            r = CL.loop_analysis(cm)
            poles = CL.label_cl_poles(cm, fmax_hz=2.0)
            osc = [p for p in poles if p['im'] > 1e-9]
            zmin = min(osc, key=lambda p: p['zeta']) if osc else None
            dom = self._speed_pole(poles)
            row = dict(op=i, wind=om.wind, region=cm.region, region_name=cm.region_name, Kp=cm.Kp, Ki=cm.Ki,
                       VS_Kp=cm.VS_Kp, VS_Ki=cm.VS_Ki, Kp_float=cm.Kp_float, fl_mode=cm.fl_mode,
                       gm_db=r['gm_min']['gm_db'] if r['gm_min'] else None,
                       w_gm=r['gm_min']['w'] if r['gm_min'] else None,
                       pm=r['pm_min']['pm'] if r['pm_min'] else None, w_pm=r['pm_min']['w'] if r['pm_min'] else None,
                       dm=r['delay_margin'], s_m=r['s_m'], w_sm=r['w_sm'], Smax_db=20 * math.log10(r['Smax']),
                       Tmax_db=20 * math.log10(r['Tmax']) if r['Tmax'] > 0 else None, bw_T=r['bw_T'],
                       P=r['P'], Z=r['Z'], N=r['N_cw'], nyquist_ok=r['nyquist_ok'], stable=r['stable'],
                       disk_gm_db=r['disk_gm_db'] if r['stable'] else None, disk_pm=r['disk_pm'] if r['stable'] else None,
                       w_disk=r['w_disk'], both=cm.both,
                       min_zeta=zmin['zeta'] if zmin else None, min_zeta_f=zmin['fn'] if zmin else None,
                       min_zeta_label=zmin['label'] if zmin else None,
                       speed_pole=dom, notes=cm.notes, meas=cm.meas_labels)
            return row
        except Exception as e:
            return dict(op=i, wind=om.wind, error=f'{type(e).__name__}: {e}')

    @staticmethod
    def _speed_pole(poles):
        """Closed-loop pole carrying most of the rotor-speed state + pitch/torque PI integrator."""
        def score(p):
            return sum(v for k, v in p['top'] if 'Rotor azimuth' in k or 'PI integrator' in k)
        ranked = sorted([p for p in poles if p['fn'] < 0.5], key=score, reverse=True)
        if not ranked or score(ranked[0]) < 0.1:
            return None
        p = ranked[0]
        return dict(wn=p['wn'], zeta=p['zeta'], re=p['re'], im=p['im'], share=score(p),
                    kind='complex pair' if abs(p['im']) > 1e-9 else 'real pole')

    # ------------------------------------------------------------------ summary
    def _design(self):
        """ROSCO 1-DOF design model vs the linearisation, per operating point."""
        out = []
        cp = None
        if self.cp is not None:
            cp = tuning.Surface(self.cp['Cp'], self.cp['pitch_rad'], self.cp['tsr'])
        turb, ctrl, src = tuning.turbine_from_sources(self.discon, self.yaml, self.fast)
        tuned = None
        if cp is not None and self.yaml is not None and all(turb.get(k) for k in ('J', 'rho', 'R', 'rated_rotor_speed', 'v_rated')):
            try:
                tuned = tuning.tune(cp, turb, ctrl)
            except Exception as e:
                tuned = dict(error=str(e))
        for i, om in enumerate(self.models):
            try:
                out.append(self._design_row(i, om, cp, turb, tuned))
            except Exception as e:                       # never let one point stop the whole analysis
                out.append(dict(op=i, wind=om.wind, error=f'{type(e).__name__}: {e}'))
        return out, turb, src, tuned

    def _design_row(self, i, om, cp, turb, tuned):
        """One operating point of the 1-DOF design-model comparison."""
        row = dict(op=i, wind=om.wind)
        tr = om.trim()
        si, so = om.std_inputs(), om.std_outputs()
        # identified from the full linear model: the real mode dominated by rotor speed
        try:
            if si['pitch'] is not None and so['genspeed'] is not None:
                s = SISO(om, si['pitch'], so['genspeed'])
                lam, V, W = om._eig_raw
                rs = [i_ for i_, d in enumerate(om.x_dof) if d[0].startswith('Rotor azimuth')]
                best, bk = 0.0, None
                for k in range(len(lam)):
                    if abs(lam[k].imag) > 1e-9 or abs(lam[k]) < 1e-9:
                        continue
                    pk = np.abs(W[k, :] * V[:, k])
                    sh = pk[rs].sum() / (pk.sum() or 1)
                    if sh > best:
                        best, bk = sh, k
                if bk is not None and best > 0.3:
                    row.update(A_lin=float(lam[bk].real), B_lin=float((s.res[bk] * math.pi / 30).real),
                               A_lin_share=float(best))
        except Exception:
            pass
        if cp is not None and turb.get('J') and turb.get('R') and turb.get('rho'):
            tsr = tr['tsr']
            if not (np.isfinite(tsr) and tsr > 0) and om.wind > 0:
                tsr = tr['rotor_rad_s'] * turb['R'] / om.wind
            pitch = tr['pitch_rad']
            if np.isfinite(pitch) and np.isfinite(tsr) and tsr > 0 and om.wind > 0:
                d = tuning.one_dof(cp, turb['rho'], turb['R'], turb['J'], turb['Ng'], om.wind, tsr, pitch)
                row.update(A_cp=d['A'], B_cp=d['B_beta'], Cp_table=d['Cp'])
        if row.get('A_lin') is not None and row.get('A_cp') is not None and row['A_lin'] < 3 * row['A_cp'] and om.wind < (turb.get('v_rated') or 0):
            row['torque_law_in_plant'] = True
        if tuned is not None and 'v' in tuned:
            row['A_rosco'] = float(np.interp(om.wind, tuned['v'], tuned['A']))
            row['B_rosco'] = float(np.interp(om.wind, tuned['v'], tuned['B_beta']))
            if om.wind >= tuned['v_pc'][0]:
                row['omega_target'] = float(np.interp(om.wind, tuned['v_pc'], tuned['omega_pc_U']))
                row['zeta_target'] = float(np.interp(om.wind, tuned['v_pc'], tuned['zeta_pc_U']))
        clr = next((c for c in self.cl_summary if c.get('op') == i), None)
        if clr and 'Kp' in clr and clr.get('region') == 'pitch':
            A_, B_ = row.get('A_cp', row.get('A_lin')), row.get('B_cp', row.get('B_lin'))
            if A_ is not None and B_ and B_ * clr['Ki'] > 0:
                wn = math.sqrt(B_ * clr['Ki'])
                row.update(omega_implied=wn, zeta_implied=(B_ * clr['Kp'] - A_) / (2 * wn))
            if clr.get('speed_pole'):
                row.update(omega_achieved=clr['speed_pole']['wn'], zeta_achieved=clr['speed_pole']['zeta'],
                           achieved_kind=clr['speed_pole']['kind'], achieved_share=clr['speed_pole']['share'])
        return row

    def _build_summary(self, pending=False):
        """pending: the closed loop is still being computed (open-loop results are complete)."""
        ms = self.models
        ref = ms[0]
        ops = []
        for i, om in enumerate(ms):
            tr = om.trim()
            ops.append(dict(i=i, **tr, n_az=om.n_azimuth, periodicity_raw=om.periodicity_raw,
                            periodicity_mbc=om.periodicity_mbc, warnings=getattr(om, 'scan_warnings', []),
                            files=[os.path.basename(f) for f in om.files],
                            omega_check=getattr(om, 'omega_check', {}), file_diag=om.file_diag,
                            az_sens=self._az_sens(om)))
        dof_fams = Counter(d[1] for d in ref.x_dof)
        dofs = sorted({d[0] for d in ref.x_dof if d[1] != 'hydro/aero states'})
        blade = any(d[1] in ('blade flap', 'blade edge', 'blade torsion') for d in ref.x_dof)
        try:
            design, turb, src, tuned = self._design()
            design_error = None
        except Exception as e:
            traceback.print_exc()
            design, turb, src, tuned = [dict(op=i, wind=m.wind) for i, m in enumerate(ms)], {}, {}, None
            design_error = f'Design-model comparison skipped: {type(e).__name__}: {e}'
        for d in design:
            if d.get('torque_law_in_plant'):
                for c in self.cl_summary:
                    if c.get('op') == d['op'] and c.get('region') == 'torque':
                        c.setdefault('notes', []).append(
                            f"Open-loop rotor-speed pole {d['A_lin']:.3g} 1/s is much faster than the aerodynamic "
                            f"value {d['A_cp']:.3g} 1/s from the Cp table: the linearisation appears to include a "
                            "generator-torque law (ServoDyn VSContrl), so the ROSCO torque loop is closed on top of it.")
        power = [o['power_kw'] for o in ops if o['power_kw'] is not None and np.isfinite(o['power_kw'])]
        info = dict(
            title=ref.title, modules=ref.modules, n_ops=len(ms), n_files=sum(len(m.files) for m in ms),
            wind_range=[ms[0].wind, ms[-1].wind], n_states=ref.A.shape[0], n_inputs=ref.B.shape[1],
            n_outputs=ref.C.shape[0], rated_power_kw=max(power) if power else None,
            max_rotor_rpm=max(o['rotor_rpm'] for o in ops), dofs=dofs, state_families=dict(dof_fams),
            has_blade_dofs=blade, removed_states=ref.removed_states,
            rotor_triplets=len(ref.plan.x_pos2) + len(ref.plan.x_first),
            lin_dir=self.cfg.get('lin_dir'), discon=self.cfg.get('discon'), yaml=self.cfg.get('yaml'),
            cp=self.cfg.get('cp'), fst=self.cfg.get('fst'),
            discon_header=self._discon_header(), turbine_params=turb, turbine_sources=src,
            name=os.path.splitext(os.path.basename(self.cfg.get('discon') or ''))[0].replace('_DISCON', '') or ref.title,
            version=__version__, name_order_differs=self.scan.get('name_order_differs'),
            scan_warnings=self.scan.get('warnings', []) + ([design_error] if design_error else [])
            + [f"{d['wind']:g} m/s design model: {d['error']}" for d in design if d.get('error')])
        ctrl = None
        if self.discon:
            d = self.discon
            gs, tt = CL.notch_sets(d)
            ctrl = dict(
                PC_GS_angles=rosco_io.as_list(d.get('PC_GS_angles')), PC_GS_KP=rosco_io.as_list(d.get('PC_GS_KP')),
                PC_GS_KI=rosco_io.as_list(d.get('PC_GS_KI')), VS_KP=rosco_io.as_list(d.get('VS_KP')),
                VS_KI=rosco_io.as_list(d.get('VS_KI')), Fl_Kp=rosco_io.as_list(d.get('Fl_Kp')),
                Fl_U=rosco_io.as_list(d.get('Fl_U')),
                settings=[[k, d.get(k)] for k in ('F_LPFType', 'F_LPFCornerFreq', 'F_LPFDamping', 'VS_ControlMode',
                                                  'VS_ConstPower', 'PC_ControlMode', 'Fl_Mode', 'F_FlCornerFreq',
                                                  'F_FlHighPassFreq', 'PA_Mode', 'PA_CornerFreq', 'PA_Damping',
                                                  'PC_RefSpd', 'VS_RtTq', 'VS_RtPwr', 'PS_Mode', 'WE_Mode') if k in d],
                gs_notches=gs, tt_notches=tt,
                tuned=None if tuned is None or 'v' not in tuned else dict(
                    v=tuned['v'], A=tuned['A'], B_beta=tuned['B_beta'], v_pc=tuned['v_pc'],
                    pc_Kp=tuned['pc_Kp'], pc_Ki=tuned['pc_Ki'], PC_GS_angles=tuned['PC_GS_angles'],
                    omega_pc_U=tuned['omega_pc_U'], zeta_pc_U=tuned['zeta_pc_U'], pitch_op=tuned['pitch_op']))
        ch = ref.channel_lists()
        si, so = ref.std_inputs(), ref.std_outputs()
        defaults = dict(
            input=si['pitch'] if si['pitch'] is not None else 0,
            output=so['genspeed'] if so['genspeed'] is not None else 0,
            below_input=si['torque'], wind_input=si['wind'])
        return clean(dict(info=info, ops=ops, campbell=self.campbell, closed=self.cl_summary, design=design,
                          controller=ctrl, channels=ch, defaults=defaults, std_inputs=si, std_outputs=so,
                          has_controller=self.discon is not None, mode=self.cfg.get('mode', 'both'),
                          closed_pending=bool(pending), dt=getattr(self, 'dt', None)))

    @staticmethod
    def _az_sens(om):
        """Worst disagreement between the single-azimuth models and the averaged one, with mode labels."""
        rows = getattr(om, 'az_modes', [])
        if not rows:
            return None
        modes = analyse_modes(om)

        def label(r):
            m = min(modes, key=lambda m: abs(complex(m['re'], m['im']) - complex(r['re'], r['im'])))
            return m['label'], m['family']
        struct = [r for r in rows if label(r)[1] != 'hydro/aero states'] or rows
        wf = max(struct, key=lambda r: max(abs(v) for v in r['df']))
        wz = max(struct, key=lambda r: max(abs(v) for v in r['dz']))
        return dict(df=max(abs(v) for v in wf['df']), df_mode=label(wf)[0], df_fn=wf['fn'],
                    dz=max(abs(v) for v in wz['dz']), dz_mode=label(wz)[0], dz_zeta=wz['zeta'],
                    modes=[dict(label=label(r)[0], fn=r['fn'], zeta=r['zeta'], df=r['df'], dz=r['dz'])
                           for r in sorted(struct, key=lambda r: r['fn'])[:12]])

    def _discon_header(self):
        p = self.cfg.get('discon')
        if not p:
            return None
        try:
            with open(p, errors='replace') as fh:
                return ' '.join(fh.readline().strip('! \n') for _ in range(2))
        except OSError:
            return None

    # ------------------------------------------------------------------ requests
    def modes(self, i):
        om = self.models[i]
        ms = mode_table(analyse_modes(om))
        return clean(dict(op=i, wind=om.wind, modes=ms))

    def campbell_data(self, include_nonstructural=False, max_zeta=0.9, closed=False):
        if closed:
            self._need_cl()
            if self.campbell_cl is None:
                per_op = []
                for i in range(len(self.models)):
                    try:
                        per_op.append(CL.cl_modes(self.cm(i)))
                    except Exception:
                        per_op.append([])
                self.campbell_cl = clean(track_modes(per_op, self.models))
            return self.campbell_cl
        if include_nonstructural:
            if self.campbell_all is None:
                self.campbell_all = campbell(self.models, include_nonstructural=True, max_zeta=max_zeta)
            return clean(self.campbell_all)
        return clean(self.campbell)

    def tf(self, i, i_in, i_out):
        om = self.models[i]
        s = SISO(om, i_in, i_out)
        r = s.analyse()
        w, mag, ph, re, im = _thin(np.array(r['w']), r['mag'], r['phase'], r['re'], r['im'], n=1200)
        r.update(w=w, mag=mag, phase=ph, re=re, im=im)
        r['op'], r['wind'] = i, om.wind
        r['modal'] = lti.modal_export(om.A, s.b, s.c, s.d, s.scale)
        return clean(r)

    def time(self, i, i_in, i_out, kind='step', amp=1.0, T=None):
        om = self.models[i]
        s = SISO(om, i_in, i_out)
        t, y, T = s.time(kind, amp, T)
        m = step_metrics(t, y, kind)
        return clean(dict(t=t, y=y, T=T, metrics=m, units=dict(input=s.in_unit, output=s.out_unit),
                          labels=dict(input=s.in_label, output=s.out_label), op=i, wind=om.wind,
                          modal=lti.modal_export(om.A, s.b, s.c, s.d, s.scale, time=(T, kind))))

    def sweep(self, i_in, i_out):
        rows = []
        w_all = np.logspace(-3, np.log10(40), 500)
        for i, om in enumerate(self.models):
            s = SISO(om, i_in, i_out)
            G = s.response(w_all)
            info, rel = s.pole_info()
            r = s.analyse()
            m = r['metrics']
            rows.append(dict(op=i, wind=om.wind, mag=np.abs(G), phase=np.degrees(np.unwrap(np.angle(G))),
                             dc=m['dc_gain'], peak=m['peak_gain'], peak_w=m['peak_w'], bw=m['bandwidth_w'],
                             rhp_zeros=m['n_rhp_zeros'], rhp_poles=m['n_rhp_poles'],
                             zeros=r['zeros'], poles=[p for p in r['poles'] if p['rel'] > 1e-3],
                             modal=lti.modal_export(om.A, s.b, s.c, s.d, s.scale)))
        s0 = SISO(self.models[0], i_in, i_out)
        return clean(dict(w=w_all, rows=rows, units=dict(input=s0.in_unit, output=s0.out_unit),
                          labels=dict(input=s0.in_label, output=s0.out_label)))

    def _channels(self, ins, outs):
        om = self.models[0]
        ins = [i for i in dict.fromkeys(int(i) for i in ins) if 0 <= i < om.B.shape[1]]
        outs = [i for i in dict.fromkeys(int(i) for i in outs) if 0 <= i < om.C.shape[0]]
        if not ins or not outs:
            raise ValueError('Choose at least one input and one output.')
        return ins, outs

    def structure(self, i, ins, outs):
        """Controllability, observability and Hankel singular values at one operating point."""
        ins, outs = self._channels(ins, outs)
        om = self.models[i]
        r = ST.analyse(om, analyse_modes(om), ins, outs)
        r['op'], r['wind'] = i, om.wind
        return clean(r)

    def structure_sweep(self, ins, outs, n_hsv=10):
        ins, outs = self._channels(ins, outs)
        rows = []
        for i, om in enumerate(self.models):
            try:
                r = ST.analyse(om, analyse_modes(om), ins, outs)
                rows.append(dict(op=i, wind=om.wind, n=r['n'], rank_c=r['rank_c'], rank_o=r['rank_o'],
                                 n_min=r['n_min'], hsv=r['hsv'][:n_hsv], order_1pct=r['order_1pct'],
                                 n_unstable=r['n_unstable'], stabilizable=r['stabilizable'],
                                 detectable=r['detectable'],
                                 uncontrollable=[u['label'] for u in r['uncontrollable']],
                                 unobservable=[u['label'] for u in r['unobservable']]))
            except Exception as e:
                rows.append(dict(op=i, wind=om.wind, error=f'{type(e).__name__}: {e}'))
        return clean(dict(rows=rows))

    # ------------------------------------------------------------------ closed loop requests
    # ------------------------------------------------------------------ gain study
    def _need_cl(self):
        if not self.discon:
            raise ValueError('The gain study needs a DISCON.IN (closed loop).')

    def gain_map(self, i, pair='pi', lo=0.25, hi=4.0, n=25):
        self._need_cl()
        n = int(min(max(n, 5), 61))
        key = ('map', i, self.region_override.get(i, 'auto'), pair, round(lo, 6), round(hi, 6), n)
        if key not in self._gcache:
            cm = self.cm(i)
            r = GS.gain_map(cm, pair, lo, hi, n)
            r.update(op=i, wind=cm.om.wind, region=cm.region, region_name=cm.region_name, names=GS.gains_of(cm),
                     fl_mode=cm.fl_mode, kp_float=cm.Kp_float, target=self._gain_target(i, cm))
            self._gcache[key] = clean(r)
        return self._gcache[key]

    def _gain_target(self, i, cm):
        """YAML-tuned gains at this operating point as factors of the DISCON gains (if available)."""
        tuned = (self.summary or {}).get('controller', {}) if self.summary else {}
        tuned = (tuned or {}).get('tuned')
        if not tuned or cm.region != 'pitch' or not cm.Kp or not cm.Ki:
            return None
        pitch = cm.trim['pitch_rad']
        ang = np.asarray(tuned['PC_GS_angles'], float)
        o = np.argsort(ang)
        kp = float(np.interp(pitch, ang[o], np.asarray(tuned['pc_Kp'], float)[o]))
        ki = float(np.interp(pitch, ang[o], np.asarray(tuned['pc_Ki'], float)[o]))
        return dict(fx=kp / cm.Kp, fy=ki / cm.Ki, kp=kp, ki=ki)

    def gain_point(self, i, fx, fy, pair='pi'):
        self._need_cl()
        key = ('pt', i, self.region_override.get(i, 'auto'), pair, round(fx, 9), round(fy, 9))
        if key not in self._gcache:
            self._gcache[key] = clean(GS.point(self.cm(i), fx, fy, pair))
        return self._gcache[key]

    def gain_scaling(self):
        self._need_cl()
        key = ('scale', tuple(sorted(self.region_override.items())))
        if key not in self._gcache:
            cms = []
            for i in range(len(self.models)):
                try:
                    cms.append((i, self.cm(i)))
                except Exception:
                    continue
            self._gcache[key] = clean(GS.scaling_sweep(cms))
        return self._gcache[key]

    # ------------------------------------------------------------------ delay, loop interaction
    def delay_margins(self, tau):
        """Margins with a pure loop delay tau [s] (controller time step and hold) at every operating point."""
        self._need_cl()
        tau = float(max(0.0, min(tau, 5.0)))
        key = ('delay', round(tau, 7), tuple(sorted(self.region_override.items())))
        if key not in self._gcache:
            rows = []
            for i, om in enumerate(self.models):
                try:
                    r = CL.loop_analysis(self.cm(i), tau=tau)
                    rows.append(dict(op=i, wind=om.wind, stable=r['stable'], Z=r['Z'],
                                     gm_db=r['gm_min']['gm_db'] if r['gm_min'] else None,
                                     pm=r['pm_min']['pm'] if r['pm_min'] else None, w_pm=r['pm_min']['w'] if r['pm_min'] else None,
                                     s_m=r['s_m'], disk_gm_db=r['disk_gm_db'] if r['stable'] else None,
                                     disk_pm=r['disk_pm'] if r['stable'] else None))
                except Exception as e:
                    rows.append(dict(op=i, wind=om.wind, error=f'{type(e).__name__}: {e}'))
            self._gcache[key] = clean(dict(tau=tau, rows=rows))
        return self._gcache[key]

    def interaction(self):
        """Pitch and torque PI loops closed together, against each loop alone, at every operating point."""
        self._need_cl()
        if 'interaction' in self._gcache:
            return self._gcache['interaction']

        def marg(cm):
            r = CL.loop_analysis(cm)
            return dict(stable=r['stable'], gm_db=r['gm_min']['gm_db'] if r['gm_min'] else None,
                        pm=r['pm_min']['pm'] if r['pm_min'] else None, w_pm=r['pm_min']['w'] if r['pm_min'] else None,
                        s_m=r['s_m'])
        rows = []
        rated = float(self.discon.get('PC_RefSpd') or 0) * 30 / math.pi
        for i, om in enumerate(self.models):
            try:
                mk = lambda reg: CL.ControllerModel(om, self.discon, region=reg)
                cp_, ct_, cb, cbt = mk('pitch'), mk('torque'), mk('both'), mk('both_tq')
                p, t, pb, tb = marg(cp_), marg(ct_), marg(cb), marg(cbt)
                lam = linalg.eigvals(cb.closed_loop()[0])
                # how much closing the torque loop changes the plant seen by the pitch loop, at its crossover
                coup = None
                if p['w_pm']:
                    Lt = complex(ct_.loop_eval(np.array([1j * p['w_pm']]))[0])
                    coup = -20 * math.log10(abs(1 + Lt))
                tr = om.trim()
                rows.append(dict(op=i, wind=om.wind, auto=self.cm(i).region,
                                 speed_ratio=(tr['gen_rpm'] / rated) if rated and np.isfinite(tr['gen_rpm']) else None,
                                 pitch=p, torque=t, pitch_both=pb, torque_both=tb,
                                 both_stable=bool(lam.real.max() <= 1e-7), max_re=float(lam.real.max()),
                                 n_origin=int(np.sum(np.abs(lam) < 1e-6)), coupling_db=coup))
            except Exception as e:
                rows.append(dict(op=i, wind=om.wind, error=f'{type(e).__name__}: {e}'))
        self._gcache['interaction'] = clean(dict(rows=rows))
        return self._gcache['interaction']

    def set_region(self, i, region):
        self.region_override[i] = region
        self.campbell_cl = None
        self._gcache = {k: v for k, v in self._gcache.items() if not (len(k) > 1 and k[1] == i) and k[0] != 'scale'}
        self._ccache.pop(i, None)
        row = self._cl_row(i)
        for k, c in enumerate(self.cl_summary):
            if c.get('op') == i:
                self.cl_summary[k] = row
        return clean(row)

    def closed(self, i):
        if i not in self._ccache:
            self._ccache[i] = self._closed(i)
        return self._ccache[i]

    def _closed(self, i):
        om = self.models[i]
        cm = self.cm(i)
        r = CL.loop_analysis(cm)
        w, L, S, T = r['w'], r['L'], r['S'], r['T']
        w_t, Lm, Lp, Sm, Tm = _thin(w, np.abs(L), np.degrees(np.unwrap(np.angle(L))), np.abs(S), np.abs(T), n=1200)
        # Nyquist curve (positive frequencies) with dense sampling near -1
        wn_ = np.unique(np.concatenate([np.logspace(-4, np.log10(40), 3000), w]))
        Ln = cm.loop_eval(1j * wn_)
        keep = np.abs(Ln) < 1e3
        poles = CL.label_cl_poles(cm, fmax_hz=3.0)
        ol = [dict(re=m['re'], im=m['im'], label=m['label'], fn=m['fn'], zeta=m['zeta'])
              for m in analyse_modes(om) if m['fn'] <= 3.0]
        rl = CL.root_locus(cm, fmax_hz=1.0)
        Kw = np.logspace(-3, np.log10(40), 600)
        Kh = cm.ctrl_eval(1j * Kw)[:, 0, 0]
        row = next((c for c in self.cl_summary if c.get('op') == i), None) or self._cl_row(i)
        Lss = cm.loop_system()
        Kall = cm._prep_eval()['Kall']
        return clean(dict(
            L_modal=lti.modal_export(Lss.A, Lss.B[:, 0], Lss.C[0], Lss.D[0, 0]),
            K_modal=lti.modal_export(Kall.A, Kall.B[:, 0], Kall.C[0], Kall.D[0, 0]),
            op=i, wind=om.wind, region=cm.region, region_name=cm.region_name, act=cm.act_name,
            act_unit=cm.act_unit, meas=cm.meas_labels, notes=cm.notes,
            Kp=cm.Kp, Ki=cm.Ki, VS_Kp=cm.VS_Kp, VS_Ki=cm.VS_Ki, Kp_float=cm.Kp_float, fl_mode=cm.fl_mode,
            w=w_t, L_mag=Lm, L_phase=Lp, S_mag=Sm, T_mag=Tm,
            nyq_w=wn_[keep], nyq_re=Ln.real[keep], nyq_im=Ln.imag[keep],
            gm=r['gm'], pm=r['pm'], gm_min=r['gm_min'], pm_min=r['pm_min'], dm=r['delay_margin'],
            s_m=r['s_m'], w_sm=r['w_sm'], Smax=r['Smax'], w_Smax=r['w_Smax'], Tmax=r['Tmax'], w_Tmax=r['w_Tmax'],
            bw_T=r['bw_T'], P=r['P'], Z=r['Z'], N=r['N_cw'], nyquist_ok=r['nyquist_ok'], stable=r['stable'],
            disk_gm_db=r['disk_gm_db'] if r['stable'] else None, disk_pm=r['disk_pm'] if r['stable'] else None,
            cl_poles=poles, ol_poles=ol, root_locus=rl, ctrl_w=Kw, ctrl_mag=np.abs(Kh),
            ctrl_phase=np.degrees(np.unwrap(np.angle(Kh))), summary=row,
            blocks=[s for s in dict.fromkeys(cm.K.states)]))

    def closed_time(self, i, i_in, i_out, kind='step', amp=1.0, T=None, out_kind='plant'):
        """Closed- vs open-loop response to a plant-input disturbance."""
        om = self.models[i]
        cm = self.cm(i)
        A, B, C, D, states = cm.closed_loop()
        s = SISO(om, i_in, i_out) if out_kind == 'plant' else None
        fin = unit_convert(om.u[i_in].unit)[1]
        if out_kind == 'plant':
            c, d = C[i_out], D[i_out, i_in]
            fout = unit_convert(om.y[i_out].unit)[1]
            ounit, olabel = unit_convert(om.y[i_out].unit)[0], om.y_label[i_out]
        else:
            c, d = C[om.C.shape[0]], D[om.C.shape[0], i_in]
            fout = 180 / math.pi if cm.act_unit == 'rad' else 1.0
            ounit = 'deg' if cm.act_unit == 'rad' else cm.act_unit
            olabel = f'controller command: {cm.act_name}'
        if T is None:
            lam = linalg.eigvals(A)
            sl = [abs(l.real) for l in lam if abs(l.real) > 1e-5 and abs(l) < 20]
            T = float(np.clip(6.0 / min(sl), 30.0, 600.0)) if sl else 200.0
        t, ycl = lti.step_impulse(A, B[:, i_in], c, d, kind, amp / fin, T, 1500)
        ycl = ycl * fout
        res = dict(t=t, y_cl=ycl, metrics_cl=step_metrics(t, ycl, kind), T=T, op=i, wind=om.wind,
                   units=dict(input=unit_convert(om.u[i_in].unit)[0], output=ounit),
                   labels=dict(input=om.u_label[i_in], output=olabel),
                   cl_modal=lti.modal_export(A, B[:, i_in], c, d, fout / fin, time=(T, kind)))
        if s is not None:
            t2, yol = lti.step_impulse(om.A, om.B[:, i_in], om.C[i_out], om.D[i_out, i_in], kind, amp / fin, T, 1500)
            res.update(y_ol=yol * fout, metrics_ol=step_metrics(t2, yol * fout, kind),
                       ol_modal=lti.modal_export(om.A, om.B[:, i_in], om.C[i_out], om.D[i_out, i_in], fout / fin,
                                                 time=(T, kind)))
        return clean(res)

    def closed_tf(self, i, i_in, i_out):
        """Closed-loop frequency response from a plant input (disturbance) to a plant output, vs open loop."""
        om = self.models[i]
        cm = self.cm(i)
        A, B, C, D, _ = cm.closed_loop()
        w = freq_grid(linalg.eigvals(om.A), 1e-3, 40.0)
        w = w[::max(1, len(w) // 900)]
        Gc = lti.freqresp(A, B[:, [i_in]], C[[i_out]], D[[i_out]][:, [i_in]], w)[:, 0, 0]
        Go = lti.freqresp(om.A, om.B[:, [i_in]], om.C[[i_out]], om.D[[i_out]][:, [i_in]], w)[:, 0, 0]
        fin, fout = unit_convert(om.u[i_in].unit)[1], unit_convert(om.y[i_out].unit)[1]
        sc = fout / fin
        return clean(dict(w=w, cl_mag=np.abs(Gc) * sc, ol_mag=np.abs(Go) * sc,
                          cl_modal=lti.modal_export(A, B[:, i_in], C[i_out], D[i_out, i_in], sc),
                          ol_modal=lti.modal_export(om.A, om.B[:, i_in], om.C[i_out], om.D[i_out, i_in], sc),
                          cl_phase=np.degrees(np.unwrap(np.angle(Gc))), ol_phase=np.degrees(np.unwrap(np.angle(Go))),
                          units=dict(input=unit_convert(om.u[i_in].unit)[0], output=unit_convert(om.y[i_out].unit)[0]),
                          labels=dict(input=om.u_label[i_in], output=om.y_label[i_out])))
