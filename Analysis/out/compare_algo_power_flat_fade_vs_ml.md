## wwwroot/test-algo-power-flat-fade.db vs wwwroot/test-ml.db

players with PP: 144722 vs 144722; Spearman of player PP 0.99847
- top 10: overlap 7/10; mean PP 24230 vs 22584 (+7.3%)
- top 100: overlap 81/100; mean PP 20877 vs 19667 (+6.2%)
- top 1000: overlap 905/1000; mean PP 16530 vs 16393 (+0.8%)
- base top-1000 players: PP change p5/p50/p95 -7.4% / -0.0% / +8.6%; |rank change| median 88, p90 271, max 585
- base rank 1-100: 100 players, PP change median +4.6% (p10 -0.1%, p90 +11.5%)
- base rank 101-1000: 900 players, PP change median -0.5% (p10 -6.1%, p90 +5.3%)
- base rank 1001-10000: 9000 players, PP change median -5.6% (p10 -8.8%, p90 -1.0%)
- base rank 10001-50000: 40000 players, PP change median +0.0% (p10 -4.7%, p90 +6.6%)
- base rank 50001-end: 94722 players, PP change median +5.7% (p10 -3.6%, p90 +23.2%)
- top-1000 PP composition (test): pass 9.6%, acc 83.3%, tech 7.1%
- top-1000 PP composition (base): pass 25.2%, acc 68.9%, tech 5.9%

maps: 3960; stars change |d| p50 0.933, p90 2.319, max 11.06; corr 0.9761
acc rating corr 0.9448; mean 9.484 vs 8.493; predicted acc mean 0.9830 vs 0.9802
Megametric125 (maps with >=100 scores): mean 0.136 vs 0.135; SD 0.146 vs 0.155; maps >= 0.65: 1.1% vs 1.9%
Megametric (maps with >=100 scores): mean 0.185 vs 0.182; SD 0.162 vs 0.169; maps >= 0.65: 1.9% vs 2.5%
weighted-PP share per map: ratio p5/p50/p95 0.52/1.07/2.10

largest star increases:
                                    Name DifficultyName  Stars_b   Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                                    
2a9e791               Speedcore Paradise     ExpertPlus   11.148  22.211        9.614     20.034           0.978         0.944      13.300       6.694
2dd6cxx92                  Bomb The Rave     ExpertPlus   11.438  20.054       12.534     20.559           0.967         0.942       4.646      11.832
41a23xxxxx91            Unwelcome School     ExpertPlus   15.139  23.360       13.484     20.708           0.961         0.941      11.628      12.970
4324exxxx91           Gravisphere Crisis     ExpertPlus   13.318  19.629       11.977     17.676           0.969         0.955      11.796      10.370
33705xxxxxx91                        999     ExpertPlus   14.469  20.607       11.789     17.239           0.970         0.957      16.697       5.175
3e7d8xx91                         stasis     ExpertPlus   12.543  18.624       11.975     17.528           0.969         0.956       7.939      14.706
338afxx91                          Feral     ExpertPlus   14.095  19.715       12.524     17.496           0.967         0.956      13.152       8.483
3b2bcxxxx91               Count down 321     ExpertPlus   14.506  20.091       13.067     17.955           0.964         0.954      12.138      10.403
3e87191                   Muzik Overload     ExpertPlus   12.371  17.945       11.821     16.913           0.970         0.958       9.928      10.062
3ce7axxxxxxxxxxx91  The Purple Dimension     ExpertPlus   15.765  21.301       12.435     17.215           0.967         0.957      17.462       6.802

largest star decreases:
                                           Name DifficultyName  Stars_b  Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                                          
eb2871                               Holdin' On         Expert    9.563  8.042       11.592      9.454           0.971         0.984       5.144       4.384
3aae492                           Feel The Same     ExpertPlus    8.886  7.448       11.517      9.447           0.971         0.984       3.188       4.400
42a5cxxx72                              epitaxy         Expert    7.899  6.464       11.029      8.931           0.973         0.985       1.673       3.746
41cfbxxx72    The Intense Voice of Hatsune Miku         Expert    7.565  6.157       10.313      8.256           0.976         0.987       2.094       4.610
3ee4bxx72                             RTX 20000         Expert    7.551  6.238       10.289      8.348           0.976         0.987       2.498       3.826
3aae472                           Feel The Same         Expert    7.084  5.836        9.851      7.980           0.977         0.988       2.642       2.807
3d2a8xxxx92                       Sonic Blaster     ExpertPlus    9.405  8.275       12.188     10.470           0.968         0.981       2.507       5.378
3fa34xx92                   UR EMBARRASSING !!!     ExpertPlus    8.105  6.982       10.899      9.188           0.974         0.985       2.181       4.771
3e6cdxxxxx52                             lustre           Hard    8.806  7.692       11.470      9.775           0.971         0.983       2.891       4.739
4ad7exx92                        Raised by Bats     ExpertPlus    8.344  7.257       11.122      9.456           0.973         0.984       2.703       4.040

base top-500 players moving most (PP change):
                            Name         Pp  Rank       Pp_b  Rank_b      d
Id                                                                         
76561198180044686  CoolingCloset  20691.199    43  17896.303     138  0.156
76561198410971373        Blep :3  23121.988     8  20186.016      27  0.145
1922350521131465     oermergeesh  27238.844     1  23945.576       2  0.138
76561199839215664        Madz100  21824.125    27  19294.311      52  0.131
76561198085710824  wobbly shadow  18827.824   121  16653.871     338  0.131
76561198835772160     TornadoEF6  22427.209    14  19958.152      29  0.124
76561198166061709     Taichidesu  22122.312    22  19731.947      36  0.121
76561199080950125    blobby56879  24058.756     4  21464.256      12  0.121
76561198335894744           para  22708.924    12  20270.301      25  0.120
323214                     Orbit  21980.428    24  19659.371      39  0.118
