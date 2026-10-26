"""Operator-only exact-owned OCR campaign, no production mounts or provider."""
import hashlib,json,pathlib,subprocess,time,traceback,io,tarfile
ROOT=pathlib.Path('/home/maxime/saaia-ingestion-qualification/48e5eba2-ea16-41da-98a7-65472c7f6dd6')
RUN='ocr-e2e-48e5eba2ea16-v3'
BASE='saaia-qual-backend:48e5eba2ea16-final-v1'
TAG='saaia-ocr-e2e:'+RUN
OUT=ROOT/RUN
def command(args,**kwargs):
    return subprocess.run(args,check=True,text=True,capture_output=True,**kwargs)
def write(name,value):
    (OUT/name).write_text(json.dumps(value,indent=2)+'\n')
def inspect(cid):
    return json.loads(command(['docker','inspect',cid]).stdout)[0]
def guard(c):
    assert c['Id']==cid and c['Image']==image['Id']
    assert c['Config']['Labels'].get('com.saaia.validation.owner')==RUN
    h=c['HostConfig']; assert h['NetworkMode']=='none' and not h.get('Privileged')
    assert h['Memory']==2147483648 and h['NanoCpus']==2000000000
    assert not h.get('Devices') and not h.get('DeviceRequests') and not h.get('PortBindings')
    assert len(c['Mounts'])==1
    m=c['Mounts'][0]; assert m['Source']==str(OUT/'evidence') and m['Destination']=='/evidence' and m['RW']
assert ROOT.is_dir() and not OUT.exists()
OUT.mkdir(mode=0o700); (OUT/'evidence').mkdir(mode=0o777); (OUT/'evidence').chmod(0o777)
cid=None
result={'executed':True,'passed':False,'paidProviderCalls':0,'startedAtUnix':time.time()}
try:
    owner=json.loads((ROOT/'owner.json').read_text())
    expected=next(c['imageId'] for c in owner['containers'] if c['role']=='backend')
    base=json.loads(command(['docker','image','inspect',BASE]).stdout)[0]; assert base['Id']==expected
    write('backend-image.inspect.private.json',base)
    # BuildKit can reuse its prior immutable SDK stage without a locally tagged image.
    with (OUT/'build.private.log').open('w') as log:
        b=subprocess.run(['docker','build','--network','none','--pull=false','--build-arg','BACKEND_IMAGE='+BASE,'--label','com.saaia.validation.owner='+RUN,'-t',TAG,str(ROOT/'ocr-package-v2')],stdout=log,stderr=subprocess.STDOUT,timeout=900)
    assert b.returncode==0,'OCR image build failed'
    image=json.loads(command(['docker','image','inspect',TAG]).stdout)[0]
    write('campaign-image.inspect.private.json',image)
    cid=command(['docker','create','--name',RUN,'--network','none','--cpus','2','--memory','2g','--pids-limit','256','--label','com.saaia.validation.owner='+RUN,'--env','SAAIA_TEST_OCR_E2E=1','--mount','type=bind,src='+str(OUT/'evidence')+',dst=/evidence',TAG]).stdout.strip()
    before=inspect(cid); guard(before); write('container-before.inspect.private.json',before)
    with (OUT/'container.private.log').open('w') as log:
        p=subprocess.run(['docker','start','--attach',cid],stdout=log,stderr=subprocess.STDOUT,timeout=600)
    after=inspect(cid); guard(after); write('container-after.inspect.private.json',after)
    result.update({'containerId':cid,'imageId':image['Id'],'exitCode':after['State']['ExitCode'],'oomKilled':after['State']['OOMKilled'],'networkMode':after['HostConfig']['NetworkMode']})
    raw=subprocess.run(['docker','cp',cid+':/evidence/result.json','-'],check=True,capture_output=True).stdout
    assert len(raw)<1048576
    with tarfile.open(fileobj=io.BytesIO(raw)) as archive:
        members=archive.getmembers(); assert len(members)==1 and members[0].isfile() and pathlib.PurePosixPath(members[0].name).name=='result.json'
        proof=json.loads(archive.extractfile(members[0]).read())
    write('result-recovered.json',proof)
    result['testResult']=proof
    result['passed']=p.returncode==0 and after['State']['ExitCode']==0 and proof.get('passed') is True and not after['State']['OOMKilled']
except Exception as e:
    result.update({'failureType':type(e).__name__,'failure':str(e)})
    (OUT/'failure.private.txt').write_text(traceback.format_exc())
finally:
    if cid:
        c=inspect(cid); guard(c)
        if c['State']['Running']: command(['docker','stop','--time','20',cid],timeout=45)
        c=inspect(cid); guard(c); write('container-stopped.inspect.private.json',c)
        result['stopped']=not c['State']['Running']; result['passed']=result['passed'] and result['stopped']
    result['finishedAtUnix']=time.time(); write('operator-result.json',result)
print(json.dumps({'passed':result['passed'],'failureType':result.get('failureType'),'exitCode':result.get('exitCode'),'stopped':result.get('stopped')}))
