import json, re, statistics, sys
from pathlib import Path

folder = Path(sys.argv[1])
summary = json.loads((folder / 'summary.json').read_text(encoding='utf-8-sig'))
telemetry = [json.loads(s) for s in (folder / 'telemetry.jsonl').read_text().splitlines() if s]
log = (folder / 'runtime.log').read_text(encoding='utf-8-sig', errors='replace')
for phase in summary['phases']:
    rows = [r for r in telemetry if r['phase'] == phase['name']]
    if not rows:
        continue
    for key in ['cpuPercent', 'privateMiB', 'workingMiB', 'managedMiB', 'handles', 'threads']:
        values = [r[key] for r in rows]
        phase[key] = dict(mean=statistics.mean(values), peak=max(values), first=values[0], last=values[-1])
    phase['gcCollections'] = {f'gen{i}': rows[-1][f'gen{i}'] - rows[0][f'gen{i}'] for i in range(3)}
summary['completedFile'] = (folder / 'COMPLETED.txt').exists()
summary['abortedFile'] = (folder / 'ABORTED.txt').exists()
summary['failedFile'] = (folder / 'FAILED.txt').exists()
summary['warnings'] = {
    'invalidStateTransitions': len(re.findall(r'\[WARN\] \[STATE\] Invalid transition:', log)),
    'allWarnings': len(re.findall(r'\[WARN\]', log)),
    'errors': len(re.findall(r'\[ERROR\]', log)),
    'transitionRequested': len(re.findall(r'Transition #\d+ requested:', log)),
    'transitionCompleted': len(re.findall(r'Transition #\d+ completed:', log)),
    'transitionCanceled': len(re.findall(r'Transition #\d+ canceled:', log)),
    'renderFailed': len(re.findall(r'Render failed:', log)),
}
glass = [(float(a), float(b), int(c), d) for a,b,c,d in re.findall(r'fps=([\d.]+)/([\d.]+) presented=(\d+) renderer=(GPU|CPU)', log)]
summary['glassDiagnostics'] = dict(samples=len(glass), loopFpsMedian=statistics.median([x[0] for x in glass]) if glass else None, modes=sorted(set(x[3] for x in glass)))
(folder / 'analysis.json').write_text(json.dumps(summary, indent=2), encoding='utf-8')
print(json.dumps(dict(style=summary['style'], completed=summary['completedFile'], warnings=summary['warnings'], phases=[{k:p[k] for k in ['name','callbackFps','frameP95','frameP99','frameMax','mediaProcessed','transitionCalls','droppedByHarness']} for p in summary['phases']]),indent=2))
