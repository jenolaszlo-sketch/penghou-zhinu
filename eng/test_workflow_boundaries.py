"""Static policy regression tests. These do NOT prove live environment enforcement."""
from pathlib import Path
import copy
import json
import sys
import yaml

root=Path(__file__).resolve().parent.parent
def load(name): return yaml.load((root/'.github/workflows'/name).read_text(),Loader=yaml.BaseLoader)
def check(publish,rehearsal):
    triggers=publish['on']
    assert set(triggers)<= {'push','workflow_dispatch'}, 'Unauthorized publication event'
    assert triggers['push']=={'tags':['v*']}, 'Branch publication trigger'
    assert publish.get('permissions',{}).get('id-token') != 'write', 'Ambient OIDC'
    job=publish['jobs']['publish']
    assert job['environment']=='nuget', 'Missing environment gate'
    assert job['needs'] in ('verify','validate'), 'Missing qualification dependency'
    assert job['permissions']['id-token']=='write'
    for name,item in publish['jobs'].items():
        for step in item.get('steps',[]):
            command=step.get('run','')
            credential=step.get('uses','').startswith('NuGet/login@') or 'nuget push' in command
            assert not credential or name=='publish', 'Credentials outside protected publisher'
            if name=='publish': assert 'dotnet pack' not in command and 'dotnet build' not in command, 'Publisher rebuild'
        if name!='publish': assert item.get('permissions',{}).get('id-token')!='write', 'OIDC before authorization'
    for item in rehearsal['jobs'].values():
        assert item.get('permissions',{}).get('id-token')!='write', 'Rehearsal OIDC'
        for step in item.get('steps',[]):
            assert not step.get('uses','').startswith('NuGet/login@') and 'nuget push' not in step.get('run',''), 'Rehearsal publishes'
    assert 'environment' in rehearsal['jobs']['authorization-probe'], 'No hosted checkpoint probe'

pub=load('publish.yml'); rehearsal=load('release-rehearsal.yml')
cases=[('valid',lambda p:None,False),
       ('environment-bypass-attempt',lambda p:p['jobs']['publish'].pop('environment'),True),
       ('qualification-bypass-attempt',lambda p:p['jobs']['publish'].update(needs='unchecked'),True),
       ('unauthorized-branch-trigger',lambda p:p['on']['push'].update(branches=['main']),True),
       ('ambient-credential',lambda p:p['permissions'].update({'id-token':'write'}),True),
       ('publisher-rebuild',lambda p:p['jobs']['publish']['steps'].append({'run':'dotnet pack malicious.csproj'}),True)]
results=[]
for name,mutate,deny in cases:
    candidate=copy.deepcopy(pub);mutate(candidate);refused=False;reason=''
    try:check(candidate,rehearsal)
    except (AssertionError,KeyError) as ex:refused=True;reason=str(ex)
    results.append({'scenario':name,'result':'PASS' if refused==deny else 'FAIL','reason':reason,'kind':'STATIC WORKFLOW MUTATION; NOT HOSTED ENFORCEMENT'})
print(json.dumps(results,indent=2))
sys.exit(any(x['result']=='FAIL' for x in results))
