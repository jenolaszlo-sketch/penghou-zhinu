"""Self-consistent hostile ZIP metadata fixtures against the production verifier.
Run with a real package fixture; recompute its hash to test beyond hash mismatch.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import zipfile

p=argparse.ArgumentParser();p.add_argument('--root',required=True);p.add_argument('--input',required=True);p.add_argument('--output',required=True);p.add_argument('--pwsh',required=True);a=p.parse_args()
root=Path(a.root);source=Path(a.input);out=Path(a.output);out.mkdir(parents=True,exist_ok=False)
rows=[]
for case in ('nuspec-id','nuspec-version','nuspec-source','duplicate-member','symbol-source'):
    dest=out/case;shutil.copytree(source,dest)
    manifestPath=dest/'release-qualification-manifest.json';m=json.loads(manifestPath.read_text(encoding='utf-8-sig'))
    package=m['packages'][2];field='snupkg' if case=='symbol-source' else 'nupkg';path=dest/package[field]
    with zipfile.ZipFile(path) as z:entries=[(i,z.read(i)) for i in z.infolist()]
    updated=[]
    for info,content in entries:
        if info.filename.endswith('.nuspec'):
            text=content.decode('utf-8-sig')
            if case=='nuspec-id':text=text.replace(f'<id>{package["id"]}</id>','<id>Wrong.Package</id>')
            if case=='nuspec-version':text=text.replace(f'<version>{m["packageVersion"]}</version>','<version>0.0.0</version>')
            if case in ('nuspec-source','symbol-source'):text=text.replace(m['commitSha'],'0'*40)
            content=text.encode()
        updated.append((info,content))
    with zipfile.ZipFile(path,'w') as z:
        for i,c in updated:z.writestr(i,c)
        if case=='duplicate-member':z.writestr(updated[0][0],updated[0][1])
    package[field+'Sha256']=hashlib.sha256(path.read_bytes()).hexdigest()
    manifestPath.write_text(json.dumps(m),encoding='utf-8')
    result=subprocess.run([a.pwsh,'-NoProfile','-File',str(root/'eng/Verify-ReleaseArtifacts.ps1'),
        '-ManifestPath',str(manifestPath),'-ArtifactsDirectory',str(dest),'-ExpectedRepository',m['repository'],
        '-ExpectedCommitSha',m['commitSha'],'-ExpectedWorkflowPath',m['workflowPath'],'-ExpectedRunId',str(m['runId']),
        '-ExpectedVersion',m['packageVersion']],text=True,capture_output=True,encoding='utf-8')
    reason=result.stdout+result.stderr
    expected='Unsafe or duplicate archive member' if case=='duplicate-member' else 'Archive identity/version/source mismatch'
    rows.append({'scenario':case,'result':'PASS' if result.returncode and expected in reason else 'FAIL',
                 'exitCode':result.returncode,'expectedReason':expected,'output':reason})
(out/'archive-faults.json').write_text(json.dumps(rows,indent=2),encoding='utf-8')
print(json.dumps([{'scenario':r['scenario'],'result':r['result']} for r in rows],indent=2))
raise SystemExit(any(r['result']=='FAIL' for r in rows))
