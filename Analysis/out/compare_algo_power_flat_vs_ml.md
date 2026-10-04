## wwwroot/test-algo-power-flat.db vs wwwroot/test-ml.db

players with PP: 144722 vs 144722; Spearman of player PP 0.99769
- top 10: overlap 4/10; mean PP 22706 vs 22584 (+0.5%)
- top 100: overlap 88/100; mean PP 20118 vs 19667 (+2.3%)
- top 1000: overlap 964/1000; mean PP 16458 vs 16393 (+0.4%)
- base top-1000 players: PP change p5/p50/p95 -3.2% / +0.2% / +4.3%; |rank change| median 36, p90 112, max 1152
- base rank 1-100: 100 players, PP change median +2.4% (p10 -3.9%, p90 +6.4%)
- base rank 101-1000: 900 players, PP change median -0.0% (p10 -2.2%, p90 +2.7%)
- base rank 1001-10000: 9000 players, PP change median -4.4% (p10 -7.0%, p90 -1.0%)
- base rank 10001-50000: 40000 players, PP change median -0.0% (p10 -5.1%, p90 +8.5%)
- base rank 50001-end: 94722 players, PP change median +9.6% (p10 -2.8%, p90 +34.1%)
- top-1000 PP composition (test): pass 25.5%, acc 68.1%, tech 6.5%
- top-1000 PP composition (base): pass 25.2%, acc 68.9%, tech 5.9%

maps: 3960; stars change |d| p50 0.398, p90 0.932, max 6.40; corr 0.9793
acc rating corr 0.9508; mean 8.460 vs 8.493; predicted acc mean 0.9830 vs 0.9802
Megametric125 (maps with >=100 scores): mean 0.135 vs 0.135; SD 0.158 vs 0.155; maps >= 0.65: 1.7% vs 1.9%
Megametric (maps with >=100 scores): mean 0.184 vs 0.182; SD 0.172 vs 0.169; maps >= 0.65: 2.7% vs 2.5%
weighted-PP share per map: ratio p5/p50/p95 0.52/1.05/1.88

largest star increases:
                                                                                    Name DifficultyName  Stars_b   Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                                                                                    
2a9e791                                                               Speedcore Paradise     ExpertPlus   11.148  17.547        9.614     16.007           0.978         0.944      13.300       6.694
2dd6cxx92                                                                  Bomb The Rave     ExpertPlus   11.438  15.328       12.534     16.360           0.967         0.942       4.646      11.832
41a23xxxxx91                                                            Unwelcome School     ExpertPlus   15.139  18.394       13.484     16.460           0.961         0.941      11.628      12.970
1a55e91        My Album Is Out On Dance Corps So Now I Am Allowed To Do A Dancecore Yay!     ExpertPlus    9.085  11.782        8.979     11.793           0.979         0.970       9.484       6.508
4324exxxx91                                                           Gravisphere Crisis     ExpertPlus   13.318  15.904       11.977     14.401           0.969         0.955      11.796      10.370
33705xxxxxx91                                                                        999     ExpertPlus   14.469  16.983       11.789     14.100           0.970         0.957      16.697       5.175
3e7d8xx91                                                                         stasis     ExpertPlus   12.543  14.997       11.975     14.298           0.969         0.956       7.939      14.706
a7971                                                                       Boku no Pico         Expert    5.476   7.826        6.030      8.799           0.987         0.983       5.715       6.562
1ed491                                                                           Inferno     ExpertPlus   10.740  13.016       10.779     13.022           0.974         0.963       7.671      10.974
3e87191                                                                   Muzik Overload     ExpertPlus   12.371  14.549       11.821     13.874           0.970         0.958       9.928      10.062

largest star decreases:
                                           Name DifficultyName  Stars_b  Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                                          
eb2871                               Holdin' On         Expert    9.563  7.070       11.592      8.490           0.971         0.984       5.144       4.384
3aae492                           Feel The Same     ExpertPlus    8.886  6.496       11.517      8.485           0.971         0.984       3.188       4.400
3d2a8xxxx92                       Sonic Blaster     ExpertPlus    9.405  7.070       12.188      9.254           0.968         0.981       2.507       5.378
3a6abxxx92     Mystery Circles Ultra / U.U.F.O.     ExpertPlus   10.439  8.161       12.939     10.132           0.965         0.977       3.449       5.829
42a5cxxx72                              epitaxy         Expert    7.899  5.649       11.029      8.092           0.973         0.985       1.673       3.746
d4b831                             You Are Mine         Normal   10.374  8.195       12.243      9.560           0.968         0.980       5.390       5.365
3e6cdxxxxx52                             lustre           Hard    8.806  6.661       11.470      8.733           0.971         0.983       2.891       4.739
3e6cdxxxxx72                             lustre         Expert   10.509  8.367       12.900     10.257           0.965         0.977       3.473       6.426
3b5a9xxxx72                   Flashback Flicker         Expert    9.318  7.222       11.959      9.310           0.969         0.981       2.861       5.377
41cfbxxx72    The Intense Voice of Hatsune Miku         Expert    7.565  5.483       10.313      7.572           0.976         0.987       2.094       4.610

base top-500 players moving most (PP change):
                             Name         Pp  Rank       Pp_b  Rank_b      d
Id                                                                          
76561198186151129   ACC | Pandita  16598.834   372  21649.539      10 -0.233
76561198110147969       Reezonate  14068.286  1187  18006.943     127 -0.219
3022717414503908              amo  21707.311    10  18422.664      91  0.178
76561197974131273  JustCallMeJack  21218.484    15  18232.473     108  0.164
76561199080950125     blobby56879  24841.748     1  21464.256      12  0.157
76561199866655859        El Lápiz  16104.350   481  19105.260      58 -0.157
76561198308490688         octavia  21752.863     8  18822.672      68  0.156
76561198433457257          Oblivy  13713.282  1339  16239.147     435 -0.156
76561198035296773         KneeOak  14179.972  1131  16496.684     370 -0.140
76561198205546284          m1ddle  19349.248    75  17021.742     268  0.137
