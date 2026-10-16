# Speed-modifier check: modded scores vs the same player's nomod scores on equally rated maps.
# Inputs (wwwroot): rating-fit-scores.json/.bin (RatingFitExport.RunScores), rating-fit-modifiers.json (RunModifiers).
# Usage: python modifier_check.py <wwwroot path>
import json,numpy as np
import sys; W=sys.argv[1].rstrip('/\\')+'/'
meta=json.load(open(W+'rating-fit-scores.json'))
a=np.fromfile(W+'rating-fit-scores.bin',dtype='<i4').reshape(-1,4)
pl=a[:,0];lbi=a[:,1];acc=a[:,2].view('<f4').astype(float);modi=a[:,3]&0xFFFF
md=json.load(open(W+'rating-fit-modifiers.json'));C=md['Columns']
rows={r[0]:dict(zip(C,r)) for r in md['Rows']}
NEUTRAL={'DA','GN','PM','SC','SA','BE','IF'}
SPEED={'SS','FS','SF'}
kind=[]
for m in meta['Modifiers']:
    parts=[p for p in m.split(',') if p]
    sp=[p for p in parts if p in SPEED]; other=[p for p in parts if p not in SPEED]
    if len(sp)>1 or any(o not in NEUTRAL for o in other): kind.append(-1)
    else: kind.append({'':0,'SS':1,'FS':2,'SF':3}[sp[0] if sp else ''])
kind=np.array(kind)[modi]
names=['none','SS','FS','SF'];pref=['','SS','FS','SF']
L=len(meta['Leaderboards'])
P=np.full((L,4),np.nan);T=np.full((L,4),np.nan);PA=np.full((L,4),np.nan);STD=np.zeros(L,bool)
for i,lid in enumerate(meta['Leaderboards']):
    r=rows.get(lid)
    if not r: continue
    STD[i]=r['Mode']=='Standard'
    for k,p in enumerate(pref):
        if r.get(p+'PassRating') and r.get(p+'PredictedAcc') and r[p+'PredictedAcc']>0.6:
            P[i,k]=r[p+'PassRating'];T[i,k]=r[p+'TechRating'];PA[i,k]=r[p+'PredictedAcc']
ok=(kind>=0)&STD[lbi]&(acc>0.5)
k=np.where(kind<0,0,kind)
p=P[lbi,k];t=T[lbi,k];pa=PA[lbi,k]
ok&=~np.isnan(pa)
pl,lbi,acc,k,p,t,pa=[x[ok] for x in (pl,lbi,acc,k,p,t,pa)]
res=acc-pa
print('scores used',len(acc))
for j in range(4):
    s=k==j; print(f"{names[j]:4} n={s.sum():8d} players={len(np.unique(pl[s])):6d}  mean acc-pred {res[s].mean()*100:+.2f}%")
res=acc-pa
order=np.argsort(pl,kind='stable');pl,acc,k,p,t,pa,res=[x[order] for x in (pl,acc,k,p,t,pa,res)]
bounds=np.flatnonzero(np.diff(pl))+1;starts=np.r_[0,bounds];ends=np.r_[bounds,len(pl)]
WP,WT,MIN=0.75,0.75,5
out=[]
for s,e in zip(starts,ends):
    kk=k[s:e]
    if not (kk>0).any(): continue
    nm=kk==0
    if nm.sum()<MIN: continue
    np_,nt,nr,na,npa=p[s:e][nm],t[s:e][nm],res[s:e][nm],acc[s:e][nm],pa[s:e][nm]
    for i in np.flatnonzero(kk>0):
        j=s+i
        m=(np.abs(np_-p[j])<=WP)&(np.abs(nt-t[j])<=WT)
        if m.sum()<MIN: continue
        out.append((k[j],p[j],res[j]-nr[m].mean(),acc[j],na[m].mean(),pa[j],npa[m].mean(),nr.mean(),m.sum()))
o=np.array(out)
names={1:'SS',2:'FS',3:'SF'}
def rep(sel,label):
    print(label)
    for kk in (1,2,3):
        s=sel&(o[:,0]==kk)
        if s.sum()==0: continue
        dd=o[s,2]
        boot=[np.random.choice(dd,len(dd)).mean() for _ in range(200)]
        line=f"  {names[kk]} n={s.sum():6d}  mod score vs same player's nomod scores on equally rated maps: {dd.mean()*100:+.2f}% (median {np.median(dd)*100:+.2f}%, 95% CI {np.percentile(boot,2.5)*100:+.2f}..{np.percentile(boot,97.5)*100:+.2f})"
        parts=[]
        for lo,hi in [(0,4),(4,7),(7,10),(10,14),(14,99)]:
            b=s&(o[:,1]>=lo)&(o[:,1]<hi)
            if b.sum()>=30: parts.append(f"pass {lo}-{hi}: {o[b,2].mean()*100:+.2f}% (n={b.sum()})")
        print(line);print('     '+' ; '.join(parts))
np.random.seed(0)
rep(np.ones(len(o),bool),'All matched modded scores')

rep(o[:,7]>=-0.02,'Strong players (nomod scores average within 2% of predicted)')
for lo,hi,lab in [(-0.02,1,'strong (nomod within 2% of predicted)'),(-0.04,-0.02,'good (2-4% below)'),(-0.07,-0.04,'mid (4-7% below)'),(-1,-0.07,'weaker (>7% below)')]:
    g=(o[:,7]>=lo)&(o[:,7]<hi)
    print(lab)
    for kk in (2,3):
        s=g&(o[:,0]==kk)
        print(f"  {names[kk]} n={s.sum():6d} acc vs matched nomod acc {np.mean(o[s,3]-o[s,4])*100:+.2f}% | predicted {np.mean(o[s,5]-o[s,6])*100:+.2f}% | gap {np.mean(o[s,2])*100:+.2f}%")
