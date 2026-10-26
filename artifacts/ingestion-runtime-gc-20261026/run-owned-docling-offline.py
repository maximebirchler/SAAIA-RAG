"""Operator-only isolated Docling cold boot with provisioned cache and no network."""
import hashlib,json,pathlib,shutil,subprocess,time,traceback
ROOT=pathlib.Path('/home/maxime/saaia-ingestion-qualification/48e5eba2-ea16-41da-98a7-65472c7f6dd6')
OLD=pathlib.Path('/home/maxime/saaia-ingestion-qualification/beefbf62-feff-4faf-9cda-94aa2ef9bb7d')
RUN='docling-offline-48e5eba2ea16-v1'; OUT=ROOT/RUN
assert ROOT.is_dir() and OLD.is_dir() and not OUT.exists()
OUT.mkdir(mode=0o700)
for name in ('cache','probe','evidence'): (OUT/name).mkdir(mode=0o755)
(OUT/'evidence').chmod(0o777)
def cmd(args,**kw): return subprocess.run(args,check=True,capture_output=True,text=True,**kw)
def write(name,value): (OUT/name).write_text(json.dumps(value,indent=2)+'\n')
def inspect(cid): return json.loads(cmd(['docker','inspect',cid]).stdout)[0]
owner=json.loads((ROOT/'owner.json').read_text()); old=json.loads((OLD/'owner.json').read_text())
image=next(c['imageId'] for c in owner['containers'] if c['role']=='docling')
source=ROOT/'corpus/gold/sources/fr-scan-critical-v2.pdf'
assert source.is_file() and not source.is_symlink()
shutil.copyfile(ROOT/'offline-docling-probe.py',OUT/'probe/offline-docling-probe.py')
shutil.copyfile(ROOT/'corpus/gold/manifest.json',OUT/'probe/gold-manifest.json')
assert hashlib.sha256((OUT/'probe/offline-docling-probe.py').read_bytes()).hexdigest()=='372bbea3cc7399f6bf0c7f98f125e26329a23fcc8f6425ce269eca9fef3b72d9'
oldrec=next(c for c in old['containers'] if c['role']=='docling'); oldc=inspect(oldrec['id'])
assert oldc['Image']==image and not oldc['State']['Running']
cache=OLD/'cache/docling'
assert any(m['Source']==str(cache) and m['Destination']=='/opt/app-root/src/.cache' for m in oldc['Mounts'])
def helper(name,mounts,code):
    label=RUN+'-'+name
    args=['docker','create','--name',label,'--label','com.saaia.validation.owner='+label,'--network','none','--cpus','1','--memory','1g','--pids-limit','256','--user','0','--entrypoint','python']
    for src,dst,rw in mounts: args+=['--mount','type=bind,src='+str(src)+',dst='+dst+('' if rw else ',readonly')]
    cid=cmd(args+[image,'-c',code]).stdout.strip()
    try:
        c=inspect(cid); assert c['Image']==image and c['Config']['Labels']['com.saaia.validation.owner']==label
        assert c['HostConfig']['NetworkMode']=='none' and len(c['Mounts'])==len(mounts)
        for src,dst,rw in mounts: assert any(m['Source']==str(src) and m['Destination']==dst and m['RW']==rw for m in c['Mounts'])
        write(name+'-container-before.private.json',c)
        p=cmd(['docker','start','--attach',cid],timeout=300)
        c=inspect(cid); write(name+'-container-after.private.json',c); assert not c['State']['Running'] and c['State']['ExitCode']==0
        return p.stdout
    finally:
        c=inspect(cid); assert c['Config']['Labels']['com.saaia.validation.owner']==label
        if c['State']['Running']: cmd(['docker','stop','--time','15',cid],timeout=30)
        cmd(['docker','rm',cid])
cid=None; result={'executed':True,'passed':False,'paidProviderCalls':0,'startedAtUnix':time.time()}
try:
    code="import pathlib,shutil,os,json; s=pathlib.Path('/source'); d=pathlib.Path('/target'); shutil.copytree(s,d,dirs_exist_ok=True,symlinks=False); files=0; size=0\nfor p in [d,*d.rglob('*')]:\n assert not p.is_symlink(); os.chown(p,1001,1000); p.chmod(0o775 if p.is_dir() else 0o664)\n if p.is_file(): files+=1; size+=p.stat().st_size\nprint(json.dumps({'files':files,'bytes':size,'sourceMountedReadOnly':True,'targetOwnerUid':1001,'targetOwnerGid':1000}))"
    copied=helper('cache-copy',[(cache,'/source',False),(OUT/'cache','/target',True)],code)
    write('cache-copy-result.json',json.loads(copied))
    args=['docker','create','--name',RUN,'--label','com.saaia.validation.owner='+RUN,'--network','none','--cpus','2','--memory','5g','--pids-limit','512']
    env={'HOME':'/campaign-home','HF_HUB_OFFLINE':'1','TRANSFORMERS_OFFLINE':'1','DOCLING_DEVICE':'cpu','DOCLING_NUM_THREADS':'2','OMP_NUM_THREADS':'2','DOCLING_SERVE_ENG_KIND':'local','DOCLING_SERVE_ENG_LOC_NUM_WORKERS':'1','DOCLING_SERVE_ENG_LOC_SHARE_MODELS':'true','DOCLING_SERVE_ENABLE_UI':'false','DOCLING_SERVE_MAX_SYNC_WAIT':'900','SAAIA_DOCLING_UPSTREAM_TIMEOUT_SECONDS':'900'}
    for k,v in env.items(): args+=['--env',k+'='+v]
    mounts=[(OUT/'cache','/campaign-home/.cache',False),(source,'/input/fr-scan-critical-v2.pdf',False),(OUT/'probe','/probe',False),(OUT/'evidence','/evidence',True)]
    for src,dst,rw in mounts: args+=['--mount','type=bind,src='+str(src)+',dst='+dst+('' if rw else ',readonly')]
    cid=cmd(args+[image]).stdout.strip()
    c=inspect(cid); assert c['Image']==image and c['Config']['Labels']['com.saaia.validation.owner']==RUN
    h=c['HostConfig']; assert h['NetworkMode']=='none' and h['Memory']==5368709120 and h['NanoCpus']==2000000000
    assert not h.get('PortBindings') and not h.get('Privileged') and not h.get('DeviceRequests') and not h.get('Devices')
    assert c['Config']['Entrypoint']==oldc['Config']['Entrypoint'] and c['Config']['Cmd']==oldc['Config']['Cmd'] and c['Config']['User']==oldc['Config']['User']
    assert len(c['Mounts'])==4
    for src,dst,rw in mounts: assert any(m['Source']==str(src) and m['Destination']==dst and m['RW']==rw for m in c['Mounts'])
    write('container-before.inspect.private.json',c); cmd(['docker','start',cid])
    p=subprocess.run(['docker','exec',cid,'python','/probe/offline-docling-probe.py'],capture_output=True,text=True,timeout=1200)
    (OUT/'probe-console.private.txt').write_text(p.stdout+'\n'+p.stderr)
    c=inspect(cid); write('container-after.inspect.private.json',c)
    result.update({'containerId':cid,'imageId':image,'probeExitCode':p.returncode,'oomKilled':c['State']['OOMKilled'],'networkMode':h['NetworkMode'],'cacheMountedReadOnly':True})
    code="import pathlib,json; p=pathlib.Path('/proof/result.json'); print(p.read_text())"
    raw=helper('proof-read',[(OUT/'evidence','/proof',False)],code)
    proof=json.loads(raw); write('probe-result.json',proof)
    result['probeResult']=proof; result['passed']=p.returncode==0 and proof.get('passed') is True and not c['State']['OOMKilled']
except Exception as e:
    result.update({'failureType':type(e).__name__,'failure':str(e)}); (OUT/'failure.private.txt').write_text(traceback.format_exc())
finally:
    if cid:
        c=inspect(cid); assert c['Config']['Labels']['com.saaia.validation.owner']==RUN and c['Image']==image
        if c['State']['Running']: cmd(['docker','stop','--time','20',cid],timeout=45)
        c=inspect(cid); write('container-stopped.inspect.private.json',c)
        result['stopped']=not c['State']['Running']; result['passed']=result['passed'] and result['stopped']
        p=subprocess.run(['docker','logs',cid],capture_output=True,text=True); (OUT/'container.private.log').write_text(p.stdout+'\n'+p.stderr)
    result['finishedAtUnix']=time.time(); write('operator-result.json',result)
print(json.dumps({'passed':result['passed'],'failureType':result.get('failureType'),'probeExitCode':result.get('probeExitCode'),'stopped':result.get('stopped')}))
