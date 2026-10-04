## wwwroot/test-algo-cal.db vs wwwroot/test-ml.db

players with PP: 144722 vs 144722; Spearman of player PP 0.99871
- top 10: overlap 8/10; mean PP 23251 vs 22584 (+3.0%)
- top 100: overlap 85/100; mean PP 20335 vs 19667 (+3.4%)
- top 1000: overlap 960/1000; mean PP 16559 vs 16393 (+1.0%)
- base top-1000 players: PP change p5/p50/p95 -1.8% / +0.2% / +5.0%; |rank change| median 37, p90 103, max 748
- base rank 1-100: 100 players, PP change median +1.7% (p10 +0.4%, p90 +5.1%)
- base rank 101-1000: 900 players, PP change median -0.1% (p10 -1.5%, p90 +1.9%)
- base rank 1001-10000: 9000 players, PP change median -2.1% (p10 -3.9%, p90 -0.0%)
- base rank 10001-50000: 40000 players, PP change median -0.0% (p10 -3.3%, p90 +6.9%)
- base rank 50001-end: 94722 players, PP change median +4.6% (p10 -3.4%, p90 +18.7%)
- top-1000 PP composition (test): pass 24.1%, acc 70.0%, tech 5.9%
- top-1000 PP composition (base): pass 25.2%, acc 68.9%, tech 5.9%

maps: 3960; stars change |d| p50 0.385, p90 0.900, max 4.23; corr 0.9837
acc rating corr 0.9611; mean 8.608 vs 8.493; predicted acc mean 0.9799 vs 0.9802
Megametric125 (maps with >=100 scores): mean 0.136 vs 0.135; SD 0.152 vs 0.155; maps >= 0.65: 1.7% vs 1.9%
Megametric (maps with >=100 scores): mean 0.184 vs 0.182; SD 0.165 vs 0.169; maps >= 0.65: 2.4% vs 2.5%
weighted-PP share per map: ratio p5/p50/p95 0.54/1.07/1.99

largest star increases:
                                                                                Name DifficultyName  Stars_b   Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                                                                                
2a9e791                                                           Speedcore Paradise     ExpertPlus   11.148  15.379        9.614     14.108           0.978         0.958      13.300       6.694
1a55e91    My Album Is Out On Dance Corps So Now I Am Allowed To Do A Dancecore Yay!     ExpertPlus    9.085  11.503        8.979     11.701           0.979         0.971       9.484       6.508
a7971                                                                   Boku no Pico         Expert    5.476   7.893        6.030      9.037           0.987         0.979       5.715       6.562
15571                                                                  Midnight City         Expert    6.060   8.041        7.522      9.955           0.983         0.977       3.521       6.888
15591                                                                  Midnight City     ExpertPlus    7.680   9.596        8.372     10.617           0.981         0.975       4.697      10.921
4003fxx91                                                                    Bruises     ExpertPlus    6.111   7.921        7.274      9.499           0.984         0.978       4.661       6.330
5b671                                                                 No, Thank you!         Expert    5.381   7.142        6.669      8.893           0.985         0.980       3.563       6.679
259b91                                                                      Midnight     ExpertPlus    8.729  10.479        9.120     11.121           0.979         0.973       7.885       6.995
194291                                                                    Chrome Vox     ExpertPlus    9.804  11.457        9.808     11.654           0.977         0.971       7.319      11.282
63671                                                                           Burn         Expert    5.574   7.184        7.297      9.320           0.984         0.978       2.317       7.099

largest star decreases:
                                          Name DifficultyName  Stars_b  Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                                         
eb2871                              Holdin' On         Expert    9.563  7.153       11.592      8.744           0.971         0.980       5.144       4.384
3aae492                          Feel The Same     ExpertPlus    8.886  6.577       11.517      8.740           0.971         0.980       3.188       4.400
3d2a8xxxx92                      Sonic Blaster     ExpertPlus    9.405  7.143       12.188      9.511           0.968         0.978       2.507       5.378
3a6abxxx92    Mystery Circles Ultra / U.U.F.O.     ExpertPlus   10.439  8.182       12.939     10.339           0.965         0.975       3.449       5.829
42a5cxxx72                             epitaxy         Expert    7.899  5.707       11.029      8.313           0.973         0.981       1.673       3.746
3e6cdxxxxx72                            lustre         Expert   10.509  8.376       12.900     10.453           0.965         0.975       3.473       6.426
d4b831                            You Are Mine         Normal   10.374  8.247       12.243      9.794           0.968         0.977       5.390       5.365
42a5cxxx92                             epitaxy     ExpertPlus   11.040  8.953       13.246     10.882           0.963         0.974       3.482       7.776
3e6cdxxxxx52                            lustre           Hard    8.806  6.727       11.470      8.973           0.971         0.979       2.891       4.739
3b5a9xxxx72                  Flashback Flicker         Expert    9.318  7.300       11.959      9.573           0.969         0.978       2.861       5.377

base top-500 players moving most (PP change):
                               Name         Pp  Rank       Pp_b  Rank_b      d
Id                                                                            
76561198433457257            Oblivy  21945.463    14  16239.147     435  0.351
76561198118728813              Karu  20761.166    30  16342.941     410  0.270
76561198035296773           KneeOak  19584.605    66  16496.684     370  0.187
76561199034162864           norlore  18614.287   115  16059.911     483  0.159
76561199866655859          El Lápiz  21850.643    17  19105.260      58  0.144
76561199003586234              fqrb  19078.168    85  16844.455     305  0.133
76561198089913211  HypersonicSharkz  18326.346   131  16255.106     430  0.127
76561198128405856    OofsAndYippies  18997.928    90  16872.281     294  0.126
76561199690718880           blinxap  22372.363    10  19918.582      32  0.123
76561198166289091            Rocker  20024.447    44  17837.781     146  0.123
