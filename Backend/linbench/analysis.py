"""Session: load a linearisation set and serve the open-loop results the app shows:
Operating points (trim values, azimuth-file checks) and the Campbell diagram (modes tracked across wind speeds)."""
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
from . import cache
from .discover import find_lin_files, scan_folder
from .linfile import clear_cache, read_many
from .modal import analyse_modes, campbell
from .model import OpModel
from . import modeshape


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


class Session:
    def __init__(self):
        self.lock = threading.Lock()
        self.progress = dict(stage='idle', pct=0, msg='', done=False, error=None)
        self.models = []
        self.cfg = {}
        self.scan = {}
        self.summary = None
        self.campbell = None
        self.campbell_all = None
        self._lengths = None
        self._t0 = time.time()

    # ------------------------------------------------------------------ inputs
    @staticmethod
    def inspect(paths: dict):
        """Scan the linearisation folder: operating points (wind speeds) and their azimuth files."""
        out = dict(lin_dir=paths.get('lin_dir') or '', found={}, messages=[])
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
            self.summary, self.campbell, self.campbell_all, self._lengths = None, None, None, None
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
                    self._set('read', 8 + int(57 * i / len(groups)),
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
            self._set('modes', 66, 'Eigen-analysis and mode identification')
            for i, om in enumerate(models):
                self._set('modes', 66 + int(14 * i / len(models)), f'Eigen-analysis at {om.wind:g} m/s')
                ms = analyse_modes(om)
                osc = [m for m in ms if m['oscillatory'] and m['family'] != 'hydro/aero states']
                lz = min(osc, key=lambda m: m['zeta']) if osc else None
                self._log('modes', f'{om.wind:g} m/s · {len(ms)} modes, {len(osc)} oscillatory structural'
                          + (f' · least damped: {lz["label"]} {lz["fn"]:.3g} Hz, ζ = {lz["zeta"] * 100:.3g}%' if lz else ''),
                          wind=om.wind, zmin=lz['zeta'] if lz else None, label=lz['label'] if lz else None,
                          modes=[[m['fn'], m['zeta']] for m in osc if m['fn'] <= 3])
            self._set('campbell', 82, 'Tracking modes across wind speeds (MAC)')
            self.campbell = camp if camp is not None else campbell(models)
            self._log('campbell', f"{len(self.campbell['tracks'])} mode tracks joined across {len(models)} wind speeds "
                      "by mode-shape similarity (MAC) and optimal assignment")
            if fresh and cfg.get('cache'):
                cache.save(ckey, (info, groups, models, self.campbell))
            self._set('summary', 96, 'Summarising')
            self.summary = self._build_summary()
            self.summary['load_time_s'] = time.time() - t0
            self.progress['open_done'] = True
            self._log('summary', f'Done in {time.time() - t0:.1f} s')
            self._set('done', 100, 'Ready')
            self.progress['done'] = True
        except Exception as e:
            tb = traceback.format_exc()
            print(tb, flush=True)
            self.progress.update(error=f'{type(e).__name__}: {e}', done=True, trace=tb,
                                 failed_step=self.progress.get('msg') or self.progress.get('stage'))

    # ------------------------------------------------------------------ summary
    def _build_summary(self):
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
        power = [o['power_kw'] for o in ops if o['power_kw'] is not None and np.isfinite(o['power_kw'])]
        info = dict(
            title=ref.title, modules=ref.modules, n_ops=len(ms), n_files=sum(len(m.files) for m in ms),
            wind_range=[ms[0].wind, ms[-1].wind], n_states=ref.A.shape[0], n_inputs=ref.B.shape[1],
            n_outputs=ref.C.shape[0], rated_power_kw=max(power) if power else None,
            max_rotor_rpm=max(o['rotor_rpm'] for o in ops), dofs=dofs, state_families=dict(dof_fams),
            has_blade_dofs=blade, removed_states=ref.removed_states,
            rotor_triplets=len(ref.plan.x_pos2) + len(ref.plan.x_first),
            lin_dir=self.cfg.get('lin_dir'),
            name=ref.title,
            version=__version__, name_order_differs=self.scan.get('name_order_differs'),
            scan_warnings=self.scan.get('warnings', []))
        return clean(dict(info=info, ops=ops, campbell=self.campbell, mode='open'))

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

    # ------------------------------------------------------------------ requests
    def campbell_data(self, include_nonstructural=False, max_zeta=0.9):
        """Campbell diagram; include_nonstructural adds the hydrodynamic / aerodynamic state modes."""
        if not self.models:
            raise ValueError('No analysis loaded.')
        if include_nonstructural:
            if self.campbell_all is None:
                self.campbell_all = campbell(self.models, include_nonstructural=True, max_zeta=max_zeta)
            return clean(self.campbell_all)
        return clean(self.campbell)

    def mode_shape(self, op=None, k=None, scale=0.15, n_frames=48, tower_len=None, blade_len=None, hub_radius=None,
                   wind=None, mode_index=None, freq=None, include_all=False):
        """Animation frames of one mode at one operating point – the point clicked in the Campbell diagram.

        Which operating point: `op` (index), else the one closest to `wind` [m/s].
        Which mode, in this order:
          * `k`          eigenvalue index (sent by the app when the Campbell data has it),
          * `mode_index` 0 = lowest-frequency mode of the Campbell diagram at that wind speed, 1 = next, …
                         (checked against `freq` when both are given),
          * `freq`       the mode whose natural frequency [Hz] is closest.
        Turbine size: values given by the user, else ElastoDyn next to the .lin files, else NREL 5 MW."""
        if not self.models:
            raise ValueError('No analysis loaded – run the analysis first.')
        if op is None or not 0 <= int(op) < len(self.models):
            if wind is None:
                raise ValueError(f'Operating point {op} does not exist.')
            op = int(np.argmin([abs(m.wind - float(wind)) for m in self.models]))
        op = int(op)
        om = self.models[op]
        if getattr(om, '_eig_raw', None) is None:
            om._eig = None
        modes = analyse_modes(om)
        mode = next((m for m in modes if m['k'] == k), None) if k is not None else None
        if mode is None:
            # the same modes, in the same order, as the Campbell diagram shows at this wind speed
            camp = sorted((m for m in modes if m['oscillatory'] and m['zeta'] < 0.9
                           and (include_all or m['family'] != 'hydro/aero states')), key=lambda m: m['fn'])
            if not camp:
                raise ValueError(f'No oscillatory mode at {om.wind:g} m/s.')
            if mode_index is not None and 0 <= int(mode_index) < len(camp):
                mode = camp[int(mode_index)]
                # the index can shift when the app shows another state set: trust the clicked frequency then
                if freq is not None and abs(mode['fn'] - float(freq)) > 0.02 * max(float(freq), 0.05):
                    mode = min(camp, key=lambda m: abs(m['fn'] - float(freq)))
            elif freq is not None:
                mode = min(camp, key=lambda m: abs(m['fn'] - float(freq)))
            else:
                raise ValueError('Give the mode: k, mode_index or freq.')
        k = mode['k']
        if self._lengths is None:
            self._lengths = modeshape.turbine_lengths(self.cfg.get('lin_dir'))
        lengths = dict(self._lengths)
        for key, val in (('tower_len', tower_len), ('blade_len', blade_len), ('hub_radius', hub_radius)):
            if val is not None and val > 0:
                lengths[key] = float(val)
                lengths['source'] = 'entered in the app'
        r = modeshape.animation(om, k, mode['label'], lengths, scale=scale, n_frames=n_frames)
        r.update(op=op, wind=om.wind, rotor_rpm=om.omega * 30 / math.pi, sym=mode['sym'], whirl=mode['whirl'],
                 family=mode['family'])
        return clean(r)
