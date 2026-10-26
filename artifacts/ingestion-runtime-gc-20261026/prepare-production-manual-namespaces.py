"""Prepare only new owned roots/public caches. Never start a backend or production service."""
import hashlib,json,pathlib,shutil,subprocess,uuid
CAT=pathlib.Path('/home/maxime/saaia-ingestion-qualification/48e5eba2-ea16-41da-98a7-65472c7f6dd6')
PUBLIC=pathlib.Path('/home/maxime/saaia-ingestion-qualification/beefbf62-feff-4faf-9cda-94aa2ef9bb7d/cache')
PARENT=CAT.parent
catalog=json.loads((CAT/'owner.json').read_text())
image=next(c['imageId'] for c in catalog['containers'] if c['role']=='docling')
OUT=CAT/'next-production-manual-namespaces-v1.json'; assert not OUT.exists()
def docker(args,**kw): return subprocess.run(['docker',*args],check=True,text=True,capture_output=True,**kw)
def inspect(cid): return json.loads(docker(['inspect',cid]).stdout)[0]
prepared=[]
for index,(role,profile) in enumerate([('gold-production','manual'),('recipe-production','manual')]):
    run=uuid.uuid4(); root=PARENT/str(run); assert not root.exists() and root.parent==PARENT
    root.mkdir(mode=0o700); (root/'cache').mkdir(); base=19920+100*index
    owner={'schemaVersion':'saaia-linux-ingestion-runtime.v1','runId':str(run),'root':str(root),
      'project':'saaiaqual_'+run.hex[:12],'network':'saaiaqual_'+run.hex[:12]+'_validation',
      'networkPolicy':'dedicated_bridge','qualificationMode':'production',
      'documentsRoot':str(root/'documents'),'configurationPath':str(root/'deploy/deployment.config.json'),
      'database':'saaia_qual_'+run.hex[:12],'databaseUser':'saaia_qual_'+run.hex[:12],
      'qdrantCollection':'ingestion_'+run.hex,'endpoints':{k:'http://127.0.0.1:'+str(base+offset) for k,offset in [('backend',1),('docling',2),('tei',3),('qdrant',4)]},
      'baselineContainers':catalog['baselineContainers'],'containers':[],'state':'prepared','admissionProfile':profile}
    (root/'owner.json').write_text(json.dumps(owner,indent=2)+'\n')
    shutil.copytree(CAT/'tools',root/'tools',ignore=shutil.ignore_patterns('__pycache__'))
    shutil.copytree(CAT/'corpus',root/'corpus')
    shutil.copyfile(CAT/'bootstrap-owned-linux-v3.py',root/'bootstrap-owned-linux-v3.py')
    tei=json.loads((CAT/'tei-profile-frozen-v1.json').read_text()); tei['Rag']['EmbeddingsBaseUrl']=owner['endpoints']['tei']
    (root/'tei-profile-frozen-v1.json').write_text(json.dumps(tei,indent=2)+'\n')
    label='cache-prepare-'+run.hex[:12]
    code="import pathlib,shutil,os,json; s=pathlib.Path('/source'); d=pathlib.Path('/target'); shutil.copytree(s,d,dirs_exist_ok=True,symlinks=False); n=0; total=0\nfor p in [d,*d.rglob('*')]:\n assert not p.is_symlink(); os.chown(p,1001,1000); p.chmod(0o775 if p.is_dir() else 0o664)\n if p.is_file(): n+=1; total+=p.stat().st_size\nprint(json.dumps({'files':n,'bytes':total,'sourceReadOnly':True,'targetUid':1001,'targetGid':1000}))"
    cid=docker(['create','--name',label,'--label','com.saaia.validation.owner='+label,'--network','none','--cpus','1','--memory','1g','--pids-limit','256','--user','0','--entrypoint','python','--mount','type=bind,src='+str(PUBLIC)+',dst=/source,readonly','--mount','type=bind,src='+str(root/'cache')+',dst=/target',image,'-c',code]).stdout.strip()
    try:
        c=inspect(cid); assert c['Image']==image and c['Config']['Labels']['com.saaia.validation.owner']==label and c['HostConfig']['NetworkMode']=='none'
        assert len(c['Mounts'])==2
        assert any(m['Source']==str(PUBLIC) and m['Destination']=='/source' and not m['RW'] for m in c['Mounts'])
        assert any(m['Source']==str(root/'cache') and m['Destination']=='/target' and m['RW'] for m in c['Mounts'])
        (root/'cache-copy-before.private.json').write_text(json.dumps(c))
        p=docker(['start','--attach',cid],timeout=600)
        (root/'cache-copy-result.json').write_text(p.stdout)
        c=inspect(cid); assert not c['State']['Running'] and c['State']['ExitCode']==0
        (root/'cache-copy-after.private.json').write_text(json.dumps(c))
    finally:
        c=inspect(cid); assert c['Config']['Labels']['com.saaia.validation.owner']==label and c['Image']==image
        if c['State']['Running']: docker(['stop','--time','20',cid],timeout=45)
        docker(['rm',cid])
    prepared.append({'role':role,'profile':profile,'root':str(root),'runId':str(run)})
    OUT.write_text(json.dumps({'prepared':prepared,'productionMutations':0,'backendStarts':0},indent=2)+'\n')
    print(json.dumps({'preparedRole':role,'runId':str(run)}),flush=True)
