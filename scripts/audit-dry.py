#!/usr/bin/env python3
"""Inventory owned source and locate exact repeated blocks for semantic DRY review."""
import argparse
from collections import defaultdict
import hashlib
import json
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
EXTENSIONS = {'.cs', '.ts', '.tsx', '.mts', '.mjs', '.js', '.jsx', '.swift', '.rb', '.py', '.ps1', '.sh', '.razor', '.cshtml',
              '.h', '.c', '.cpp', '.m', '.mm', '.kt', '.kts', '.java', '.sql'}
EXCLUDED_PARTS = {'node_modules', 'Pods', 'bin', 'obj', 'build', '.build', 'dist', 'DerivedData', 'DerivedDataDevice'}
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--report', type=Path, required=True)
parser.add_argument('--window', type=int, default=12)
args = parser.parse_args()
if args.window < 8:
    parser.error('The repeated-block window must contain at least eight source lines.')

paths = sorted(set(subprocess.check_output(['git', 'ls-files', '--cached', '--others', '--exclude-standard', '-z'], cwd=ROOT)
                   .decode().split('\0')) - {''})
files, windows = [], defaultdict(list)
for name in paths:
    path = Path(name)
    if path.suffix not in EXTENSIONS or EXCLUDED_PARTS.intersection(path.parts) or path.name.endswith(('.g.cs', '.Designer.cs', 'ModelSnapshot.cs')):
        continue
    source = ROOT / path
    if not source.is_file():
        continue
    lines = source.read_text().splitlines()
    files.append({'path': name, 'lines': len(lines)})
    meaningful = [(index + 1, line.strip()) for index, line in enumerate(lines)
                  if line.strip() and not line.strip().startswith(('//', '#', '/*', '*', 'import ', '@using '))
                  and not (line.strip().startswith('using ') and line.strip().endswith(';')
                           and not line.strip().startswith('using var '))]
    for start in range(len(meaningful) - args.window + 1):
        block = meaningful[start:start + args.window]
        digest = hashlib.sha256('\n'.join(line for _, line in block).encode()).hexdigest()
        windows[digest].append({'path': name, 'line': block[0][0], 'end': block[-1][0]})

# Overlapping windows are evidence, not independent defects. Keep one representative
# per set of source locations; semantic review decides whether responsibilities coincide.
clones, previous = [], {}
for occurrences in windows.values():
    if len({item['path'] for item in occurrences}) < 2:
        continue
    occurrences.sort(key=lambda item: (item['path'], item['line']))
    identity = tuple(item['path'] for item in occurrences)
    if identity in previous and all(item['line'] <= earlier['end'] for item, earlier in zip(occurrences, previous[identity])):
        continue
    previous[identity] = occurrences
    clones.append(occurrences)

report = {'owned_files': len(files), 'source_lines': sum(item['lines'] for item in files), 'window': args.window,
          'files': files, 'cross_file_clone_blocks': clones,
          'scope': 'Tracked and new owned source; dependency, build, generated designers and EF model snapshots excluded; handwritten migrations included. Exact blocks do not prove or disprove semantic DRY.'}
args.report.parent.mkdir(parents=True, exist_ok=True)
args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n')
print(json.dumps({key: report[key] for key in ('owned_files', 'source_lines', 'window')}
                 | {'cross_file_clone_blocks': len(clones), 'report': str(args.report)}))
