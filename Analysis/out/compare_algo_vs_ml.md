## wwwroot/test-algo.db vs wwwroot/test-ml.db

players with PP: 144722 vs 144722; Spearman of player PP 0.99861
- top 10: overlap 5/10; mean PP 22997 vs 22584 (+1.8%)
- top 100: overlap 83/100; mean PP 20155 vs 19667 (+2.5%)
- top 1000: overlap 936/1000; mean PP 16454 vs 16393 (+0.4%)
- base top-1000 players: PP change p5/p50/p95 -7.0% / +0.3% / +5.5%; |rank change| median 68, p90 221, max 1919
- base rank 1-100: 100 players, PP change median +1.9% (p10 -4.4%, p90 +6.3%)
- base rank 101-1000: 900 players, PP change median +0.1% (p10 -4.1%, p90 +4.0%)
- base rank 1001-10000: 9000 players, PP change median -7.9% (p10 -15.4%, p90 -1.7%)
- base rank 10001-50000: 40000 players, PP change median -15.3% (p10 -20.7%, p90 -8.1%)
- base rank 50001-end: 94722 players, PP change median -16.3% (p10 -23.6%, p90 -7.1%)
- top-1000 PP composition (test): pass 25.9%, acc 67.5%, tech 6.6%
- top-1000 PP composition (base): pass 25.2%, acc 68.9%, tech 5.9%

maps: 3960; stars change |d| p50 0.946, p90 1.684, max 6.04; corr 0.9743
acc rating corr 0.9403; mean 7.399 vs 8.493; predicted acc mean 0.9830 vs 0.9802
Megametric125 (maps with >=100 scores): mean 0.130 vs 0.135; SD 0.162 vs 0.155; maps >= 0.65: 1.8% vs 1.9%
Megametric (maps with >=100 scores): mean 0.175 vs 0.182; SD 0.177 vs 0.169; maps >= 0.65: 2.6% vs 2.5%
weighted-PP share per map: ratio p5/p50/p95 0.50/0.92/2.04

largest star increases:
                                                                                    Name DifficultyName  Stars_b   Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                                                                                    
2a9e791                                                               Speedcore Paradise     ExpertPlus   11.148  17.187        9.614     15.937           0.978         0.944      13.300       6.694
2dd6cxx92                                                                  Bomb The Rave     ExpertPlus   11.438  14.953       12.534     16.269           0.967         0.942       4.646      11.832
41a23xxxxx91                                                            Unwelcome School     ExpertPlus   15.139  17.999       13.484     16.367           0.961         0.941      11.628      12.970
1a55e91        My Album Is Out On Dance Corps So Now I Am Allowed To Do A Dancecore Yay!     ExpertPlus    9.085  11.729        8.979     11.948           0.979         0.970       9.484       6.508
4324exxxx91                                                           Gravisphere Crisis     ExpertPlus   13.318  15.738       11.977     14.491           0.969         0.955      11.796      10.370
33705xxxxxx91                                                                        999     ExpertPlus   14.469  16.856       11.789     14.227           0.970         0.957      16.697       5.175
3e7d8xx91                                                                         stasis     ExpertPlus   12.543  14.845       11.975     14.400           0.969         0.956       7.939      14.706
1ed491                                                                           Inferno     ExpertPlus   10.740  13.000       10.779     13.240           0.974         0.963       7.671      10.974
4a5391                                                                         Enigma II     ExpertPlus   11.420  13.529       11.192     13.462           0.973         0.962       9.628       8.501
3e87191                                                                   Muzik Overload     ExpertPlus   12.371  14.458       11.821     14.030           0.970         0.958       9.928      10.062

largest star decreases:
                                           Name DifficultyName  Stars_b  Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                                          
eb2871                               Holdin' On         Expert    9.563  5.949       11.592      7.241           0.971         0.984       5.144       4.384
3aae492                           Feel The Same     ExpertPlus    8.886  5.396       11.517      7.234           0.971         0.984       3.188       4.400
42a5cxxx72                              epitaxy         Expert    7.899  4.496       11.029      6.710           0.973         0.985       1.673       3.746
41cfbxxx72    The Intense Voice of Hatsune Miku         Expert    7.565  4.200       10.313      5.990           0.976         0.987       2.094       4.610
3ee4bxx72                             RTX 20000         Expert    7.551  4.268       10.289      6.077           0.976         0.987       2.498       3.826
3d2a8xxxx92                       Sonic Blaster     ExpertPlus    9.405  6.234       12.188      8.381           0.968         0.981       2.507       5.378
3e6cdxxxxx52                             lustre           Hard    8.806  5.658       11.470      7.620           0.971         0.983       2.891       4.739
3aae472                           Feel The Same         Expert    7.084  3.938        9.851      5.749           0.977         0.988       2.642       2.807
3fa34xx92                   UR EMBARRASSING !!!     ExpertPlus    8.105  4.961       10.899      6.962           0.974         0.985       2.181       4.771
4ad7exx92                        Raised by Bats     ExpertPlus    8.344  5.219       11.122      7.243           0.973         0.984       2.703       4.040

base top-500 players moving most (PP change):
                             Name         Pp  Rank       Pp_b  Rank_b      d
Id                                                                          
76561198110147969       Reezonate  13261.415  1563  18006.943     127 -0.264
76561198186151129   ACC | Pandita  16370.387   419  21649.539      10 -0.244
76561198035296773         KneeOak  13174.341  1597  16496.684     370 -0.201
3022717414503908              amo  22073.137     8  18422.664      91  0.198
76561199866655859        El Lápiz  15609.911   607  19105.260      58 -0.183
76561198433457257          Oblivy  13291.849  1538  16239.147     435 -0.181
76561197974131273  JustCallMeJack  21491.844    13  18232.473     108  0.179
76561198308490688         octavia  22101.742     7  18822.672      68  0.174
76561198166289091          Rocker  14846.165   871  17837.781     146 -0.168
76561199080950125     blobby56879  24805.617     1  21464.256      12  0.156
