# Fits RatingAPI AccRating's hard map correction (HARD_MAP_KNEE / HARD_MAP_SLOPE) on scores, and simulates it.
# Rebuilds every score's pp as ReplayUtils.PpFromScore does (PpFormula constants and CurveSettings copied below: keep
# them equal to the code). Needs numpy.
import json,math,numpy as np
BASE=[(1.0,7.424),(0.999,6.241),(0.9975,5.158),(0.995,4.010),(0.9925,3.241),(0.99,2.700),(0.9875,2.303),(0.985,2.007),(0.9825,1.786),(0.98,1.618),(0.9775,1.490),(0.975,1.392),(0.9725,1.315),(0.97,1.256),(0.965,1.167),(0.96,1.094),(0.955,1.039),(0.95,1.000),(0.94,0.931),(0.93,0.867),(0.92,0.813),(0.91,0.768),(0.9,0.729),(0.875,0.650),(0.85,0.581),(0.825,0.522),(0.8,0.473),(0.75,0.404),(0.7,0.345),(0.65,0.296),(0.6,0.256),(0.0,0.0)]
def interp(acc,c):
    i=0
    while i<len(c) and c[i][0]>acc: i+=1
    if i==0:i=1
    if i>=len(c): i=len(c)-1
    t=(acc-c[i-1][0])/(c[i][0]-c[i-1][0]); return c[i-1][1]+t*(c[i][1]-c[i-1][1])
class MC:
    def __init__(s,A=1.0,C=1.0,K=4.86,W=0.6,K2=2.88,P2=2.46,Y0=0.256): s.__dict__.update(locals())
    def M(s): return (1-s.Y0)/0.35*((1-s.W)+s.W*(s.K*math.exp(s.K))/(math.exp(s.K)-1))
    def F1(s,x):
        u=(x-.6)/.35; return s.Y0+(1-s.Y0)*((1-s.W)*u+s.W*(math.exp(s.K*u)-1)/(math.exp(s.K)-1))
    def F2(s,x):
        m=s.M();u=(x-.95)/.05; return 1+m*(x-.95)+(6.424-.05*m)*(math.exp(s.K2*u**s.P2)-1)/(math.exp(s.K2)-1)
    def S(s):
        m=s.M();uA=(s.A-.95)/.05
        return m+(6.424-.05*m)*math.exp(s.K2*uA**s.P2)/(math.exp(s.K2)-1)*(s.K2*s.P2/.05)*(uA**(s.P2-1) if uA>0 else 0)
    def L(s,x):
        S=s.S();s1=S*(1-s.C);f=s.F2(s.A)
        if abs(s.A-1)<1e-9: return f+s1*(x-s.A)
        return f+s1*(x-s.A)+(S-s1)*(1-s.A)*(1-math.exp(-(x-s.A)/(1-s.A)))
    def v(s,x):
        if x<=.6: return s.Y0/.6*x
        if x<=.95: return s.F1(x)
        if x<=s.A: return s.F2(x)
        return s.L(x)
TABLE=[(0.955,dict(A=1.0,C=-5.3)),(0.960,dict(A=0.98,C=-5.3,K=4.1,W=0.7,K2=6,P2=1.4)),(0.965,dict(A=0.976,C=-5.3,K2=4,P2=2.37)),(0.970,dict(A=0.968,C=-2)),(0.975,dict(A=0.97,C=-1.7)),(0.980,dict(A=0.977,C=-1.4)),(0.985,dict(A=0.98,C=-1.4)),(0.990,dict(A=0.985,C=-0.6)),(0.995,dict(A=0.9915,C=-0.6))]
D=dict(A=1.0,C=0,K=4.86,W=0.6,K2=2.88,P2=2.46)
def row(pa):
    rows=[(p,{**D,**r}) for p,r in TABLE]
    if pa<=rows[0][0]: r=rows[0][1]
    elif pa>=rows[-1][0]: r=rows[-1][1]
    else:
        for i in range(1,len(rows)):
            if pa<=rows[i][0]:
                p,a=rows[i-1];n,b=rows[i];t=(pa-p)/(n-p);r={k:a[k]+t*(b[k]-a[k]) for k in a};break
    r=dict(r);r['C']=min(r['C'],1);return r
def curve(pa):
    if pa<=.6 or pa>=.9995: return BASE
    r=row(pa)
    if r['A']>=.9995 and r['K']==4.86 and r['W']==.6 and r['K2']==2.88 and r['P2']==2.46: return BASE
    a=max(r['A'],.951);mod=MC(A=min(a,1),C=r['C'],K=r['K'],W=r['W'],K2=r['K2'],P2=r['P2']);std=MC()
    xs=sorted({round(x,6) for x in [p[0] for p in BASE]+[.6+i*.005 for i in range(71)]+[.95+i*.0005 for i in range(101)]+[min(a,1)] if 0<=x<=1},reverse=True)
    out=[]
    for x in xs:
        b=interp(x,BASE);ref=std.v(x);val=b*mod.v(x)/ref if ref>0 else b;out.append((x,min(b,val)))
    return out
PS,PE,PO,AW,TW,TE,IB,IE,PM=12.9,2.62,30,34,0.63,1.9,650,1.3,1.042
def pp(acc,accR,passR,techR,pa):
    ip=passR/1.26
    p=max(PS*math.exp(ip**(1/PE))-PO,0)
    a=interp(acc,curve(pa))*accR*AW
    t=math.exp(TE*acc)*TW*techR
    tot=PM*IB*(p+a+t)**IE/IB**IE
    k=tot/(p+a+t); return tot,p*k,a*k,t*k
def solve(target,*r):
    lo,hi=0.5,1.0
    for _ in range(80):
        m=(lo+hi)/2
        if pp(m,*r)[0]<target: lo=m
        else: hi=m
    return lo
OLD=[(1.0,7.424),(0.999,6.241),(0.9975,5.158),(0.995,4.010),(0.9925,3.241),(0.99,2.700),(0.9875,2.303),(0.985,2.007),(0.9825,1.786),(0.98,1.618),(0.9775,1.490),(0.975,1.392),(0.9725,1.315),(0.97,1.256),(0.965,1.167),(0.96,1.101),(0.955,1.047),(0.95,1.000),(0.94,0.919),(0.93,0.847),(0.92,0.786),(0.91,0.734),(0.9,0.692),(0.875,0.606),(0.85,0.537),(0.825,0.480),(0.8,0.429),(0.75,0.345),(0.7,0.286),(0.65,0.246),(0.6,0.217),(0.0,0.0)]
OLDx=np.array([p[0] for p in OLD][::-1]);OLDy=np.array([p[1] for p in OLD][::-1])
def accRating(pa,offset=0.0019): return 15.5/np.interp(pa+offset,OLDx,OLDy)
def load():
    meta=json.load(open(W+'rating-fit-scores.json'))
    a=np.fromfile(W+'rating-fit-scores.bin',dtype='<i4').reshape(-1,4)
    md=json.load(open(W+'rating-fit-modifiers.json'));C=md['Columns']
    rows={r[0]:dict(zip(C,r)) for r in md['Rows']}
    L=meta['Leaderboards'];nL=len(L)
    R=np.full((nL,4,5),np.nan)  # speed x [pass,tech,predAcc,accRating,stars]
    mode=np.array(['']*nL,dtype=object)
    for i,lid in enumerate(L):
        r=rows.get(lid)
        if not r: continue
        mode[i]=r['Mode']
        for k,p in enumerate(['','SS','FS','SF']):
            v=[r.get(p+x) for x in ('PassRating','TechRating','PredictedAcc','AccRating','Stars')]
            if all(x is not None for x in v): R[i,k]=v
    # modifiers: speed index and multiplier
    NEG={'NA':-0.3,'NB':-0.2,'NF':-1.0,'NO':-0.2,'OP':-0.5}
    sp=[];mult=[];nf=[]
    for m in meta['Modifiers']:
        parts=[x for x in m.split(',') if x]
        s=0
        for x in parts:
            if x in('SS','FS','SF'): s={'SS':1,'FS':2,'SF':3}[x];break
        sp.append(s); mult.append(1+sum(NEG.get(x,0) for x in parts if x!='NF')); nf.append('NF' in parts)
    modi=a[:,3]&0xFFFF
    S=dict(pl=a[:,0],lb=a[:,1],acc=a[:,2].view('<f4').astype(np.float64),sp=np.array(sp)[modi],mult=np.array(mult)[modi],nf=np.array(nf)[modi],fc=(a[:,3]>>16)&1)
    return meta,R,mode,S
# curve cache by predicted acc (rounded)
_cc={}
def curve_arr(pa):
    key=round(float(pa),7)
    if key not in _cc:
        c=curve(pa); _cc[key]=(np.array([x for x,_ in c][::-1]),np.array([y for _,y in c][::-1]))
    return _cc[key]
PS,PE,PO,AW,TW,TE,IB,IE,PM=12.9,2.62,30,34,0.63,1.9,650,1.3,1.042
def score_pp(R,S,pred_override=None):
    """pp of every score (as ReplayUtils.PpFromScore). pred_override: (maps, 4 speeds) predicted acc used for the acc rating"""
    lb,sp,acc,mult=S['lb'],S['sp'],S['acc'],S['mult']
    passR=R[lb,sp,0];techR=R[lb,sp,1];pa=R[lb,sp,2]
    if pred_override is None: accR=R[lb,sp,3]
    else: accR=accRating(pred_override[lb,sp])
    # curve value per score: per (lb,sp) group, curve built from the *stored* predicted acc
    cv=np.zeros_like(acc)
    key=lb*4+sp
    order=np.argsort(key,kind='stable');ks=key[order]
    bnd=np.flatnonzero(np.diff(ks))+1;st=np.r_[0,bnd];en=np.r_[bnd,len(ks)]
    for s,e in zip(st,en):
        idx=order[s:e];l,k=divmod(int(ks[s]),4)
        p0=R[l,k,2]
        if not np.isfinite(p0): cv[idx]=np.nan;continue
        x,y=curve_arr(p0); cv[idx]=np.interp(acc[idx],x,y)
    ip=passR*mult/1.26
    p=np.maximum(PS*np.exp(np.power(np.maximum(ip,0),1/PE))-PO,0)
    a_=cv*accR*mult*AW
    t=np.exp(TE*acc)*TW*techR*mult
    tot=p+a_+t
    pp=PM*IB*np.power(tot,IE)/IB**IE
    pp=np.where(S['nf']|~np.isfinite(pp)|(acc<=0)|(acc>1),0,pp)
    return pp
def weights_and_players(S,pp,nP):
    order=np.lexsort((-pp,S['pl']))
    plo=S['pl'][order]
    first=np.r_[0,np.flatnonzero(np.diff(plo))+1]
    pos=np.arange(len(plo))-np.repeat(first,np.diff(np.r_[first,len(plo)]))
    w=np.zeros(len(pp));w[order]=np.where(pos<10000,0.965**pos,0)
    tot=np.bincount(S['pl'],weights=pp*w,minlength=nP)
    return w,tot

# ---------------------------------------------------------------------------------------------------------------------
EDGES=np.array([0.95,0.958,0.962,0.965,0.968,0.971,0.974,0.977,0.98,0.983,0.986,0.989,0.992,0.995,1.0])

def map_effect(S,R,mode,rank,nP,N):
    """Acc minus predicted acc by predicted-acc bin, after removing each player's own level (top N players, no
    negative modifiers, Standard maps). Alternating means of res = player level + bin effect."""
    pa=R[S['lb'],S['sp'],2];res=S['acc']-pa
    m=(rank[S['pl']]<=N)&(mode[S['lb']]=='Standard')&np.isfinite(pa)&(S['mult']==1)&~S['nf']&(S['acc']>0.5)
    b=np.clip(np.searchsorted(EDGES,pa[m])-1,0,len(EDGES)-2);pl=S['pl'][m];r=res[m]
    f=np.zeros(len(EDGES)-1)
    for _ in range(30):
        s=np.bincount(pl,r-f[b],minlength=nP)/np.maximum(np.bincount(pl,minlength=nP),1)
        f=np.bincount(b,r-s[pl],minlength=len(f))/np.maximum(np.bincount(b,minlength=len(f)),1)
    return f,np.bincount(b,minlength=len(f))

def fit(f,n):
    """Smooth trend fitted on 96.8-98.9%, then excess below it = -slope * (knee - predicted acc)^power"""
    mid=(EDGES[:-1]+EDGES[1:])/2
    tr=(mid>=0.968)&(mid<=0.989)
    A=np.c_[np.ones(tr.sum()),mid[tr]];c=np.linalg.lstsq(A*np.sqrt(n[tr])[:,None],f[tr]*np.sqrt(n[tr]),rcond=None)[0]
    ex=f-(c[0]+c[1]*mid);best=None;sel=mid<0.975
    for P0 in np.arange(0.965,0.9721,0.0005):
        for q in (1.0,1.5,2.0,2.5,3.0):
            x=np.maximum(0,P0-mid)**q
            k=-(np.sum(n[sel]*x[sel]*ex[sel])/max(np.sum(n[sel]*x[sel]**2),1e-30))
            err=np.sum(n[sel]*(ex[sel]+k*x[sel])**2)
            if best is None or err<best[0]: best=(err,P0,q,k)
    return best[1:],mid,ex

if __name__=='__main__':
    import sys
    # Usage: python hard_map_offset_fit.py <wwwroot> [top N players, default 100]
    # Inputs: rating-fit-scores.json/.bin (RatingFitExport.RunScores), rating-fit-modifiers.json (RunModifiers), exported
    # from the database to fit. Fits AccRating's hard map correction and simulates it on every score.
    W=sys.argv[1].rstrip('/\\')+'/'
    N=int(sys.argv[2]) if len(sys.argv)>2 else 100
    meta,R,mode,S=load();nP=len(meta['Players'])
    pp0=score_pp(R,S);w0,t0=weights_and_players(S,pp0,nP)
    rank=np.empty(nP,int);rank[np.argsort(-t0)]=np.arange(1,nP+1)
    print('player totals (check against the database): #1 %.2f  #10 %.2f  #100 %.2f  #1000 %.2f'%tuple(np.sort(t0)[::-1][[0,9,99,999]]))
    f,n=map_effect(S,R,mode,rank,nP,N)
    (knee,power,slope),mid,ex=fit(f,n)
    print(f'top {N} players, acc vs predicted after removing each player\'s level, and the excess below the trend:')
    for i in range(len(f)):
        if n[i]: print(f'  {EDGES[i]*100:5.1f}-{EDGES[i+1]*100:5.1f}%  n={n[i]:7d}  {f[i]*100:+.2f}%  excess {ex[i]*100:+.2f}%')
    print(f'fit: knee {knee*100:.2f}%, power {power}, slope {slope:.3f}  (AccRating HARD_MAP_KNEE / HARD_MAP_SLOPE, power 1 only)')
    if power!=1.0: print('  note: the best power is not 1; AccRating.EffectiveAcc is linear below the knee')
    FLOOR=0.95
    P2=R[:,:,2]-slope*np.maximum(0,knee-np.maximum(R[:,:,2],FLOOR))
    pp1=score_pp(R,S,pred_override=P2);w1,t1=weights_and_players(S,pp1,nP)
    def stars(a,p,t):
        pp_=np.maximum(12.9*np.exp(np.power(p/1.26,1/2.62))-30,0); ac=interp(.96,BASE)*a*34; te=math.exp(1.9*.96)*.63*t
        return 650*np.power(pp_+ac+te,1.3)/650**1.3
    St0=R[:,:,4];St1=St0*stars(accRating(P2),R[:,:,0],R[:,:,1])/stars(R[:,:,3],R[:,:,0],R[:,:,1])
    top=rank[S['pl']]<=100
    for nm,St,pp in (('before',St0,pp0),('after ',St1,pp1)):
        band=np.clip(np.floor(St[S['lb'],0]),0,15);line=[]
        for bb in range(5,16):
            m=top&(band==bb)&(pp>0)
            if m.sum()>50: line.append(f"{bb}:{np.median(pp[m]):.0f}")
        print(f'{nm} top 100 median pp by star band: '+'  '.join(line))
    print('player totals after: #1 %.0f  #10 %.0f  #100 %.0f  #1000 %.0f'%tuple(np.sort(t1)[::-1][[0,9,99,999]]))
