"""Inventory every archive member; compare bytes, dependencies and build metadata.
No equivalence class silently drops a differing binary or RID asset.
"""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

def digest(data): return hashlib.sha256(data).hexdigest()
def inventory(path):
    with zipfile.ZipFile(path) as z:
        names=z.namelist()
        specs=[n for n in names if n.endswith('.nuspec')]
        assert len(specs)==1
        doc=ET.fromstring(z.read(specs[0]))
        meta=doc.find('{*}metadata')
        repository=meta.find('{*}repository')
        members={n:digest(z.read(n)) for n in sorted(names)}
        return {'filename':path.name,'sha256':digest(path.read_bytes()),'size':path.stat().st_size,
            'id':meta.find('{*}id').text,'version':meta.find('{*}version').text,
            'repository':{} if repository is None else repository.attrib,
            'dependencies':[{'framework':g.attrib.get('targetFramework'),
                'dependencies':[d.attrib for d in g]} for g in meta.findall('{*}dependencies/{*}group')],
            'nuspec':z.read(specs[0]).decode('utf-8-sig'),
            'members':members,'ridAssets':[n for n in names if n.startswith('runtimes/')],
            'frameworkAssets':[n for n in names if n.startswith(('lib/','ref/'))]}

p=argparse.ArgumentParser();p.add_argument('left');p.add_argument('right');p.add_argument('output');a=p.parse_args()
left=Path(a.left);right=Path(a.right)
results=[]
for path in sorted(left.glob('*.nupkg'))+sorted(left.glob('*.snupkg')):
    x=inventory(path);other=right/path.name
    if not other.exists(): results.append({'left':x,'right':None,'result':'MISSING'});continue
    y=inventory(other)
    changed=sorted(n for n in set(x['members'])|set(y['members']) if x['members'].get(n)!=y['members'].get(n))
    binaries=[n for n in changed if n.endswith(('.dll','.so','.dylib','.pdb')) or n.startswith('runtimes/')]
    results.append({'left':x,'right':y,'archiveByteIdentical':x['sha256']==y['sha256'],
        'identityAndDependenciesEqual':all(x[k]==y[k] for k in ('id','version','dependencies')),
        'allMembersEqual':not changed,'changedMembers':changed,'changedBinariesOrRidAssets':binaries,
        'result':'BYTE IDENTICAL' if x['sha256']==y['sha256'] else 'DIFFERENT ARCHIVE; INSPECT MEMBER DELTA'})
Path(a.output).write_text(json.dumps(results,indent=2),encoding='utf-8')
print(json.dumps([{'package':x['left']['filename'],'byteIdentical':x.get('archiveByteIdentical'),
                  'identityDependenciesEqual':x.get('identityAndDependenciesEqual'),
                  'changedBinaries':x.get('changedBinariesOrRidAssets')} for x in results],indent=2))
