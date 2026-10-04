## wwwroot/test-algo-power.db vs wwwroot/test-ml.db

players with PP: 144722 vs 144722; Spearman of player PP 0.99849
- top 10: overlap 6/10; mean PP 23960 vs 22584 (+6.1%)
- top 100: overlap 88/100; mean PP 20743 vs 19667 (+5.5%)
- top 1000: overlap 964/1000; mean PP 16454 vs 16393 (+0.4%)
- base top-1000 players: PP change p5/p50/p95 -4.6% / -0.3% / +6.2%; |rank change| median 34, p90 108, max 789
- base rank 1-100: 100 players, PP change median +5.1% (p10 +0.1%, p90 +9.4%)
- base rank 101-1000: 900 players, PP change median -0.6% (p10 -3.8%, p90 +3.3%)
- base rank 1001-10000: 9000 players, PP change median -11.3% (p10 -15.3%, p90 -5.8%)
- base rank 10001-50000: 40000 players, PP change median -13.5% (p10 -16.9%, p90 -8.3%)
- base rank 50001-end: 94722 players, PP change median -10.1% (p10 -18.0%, p90 +3.8%)
- top-1000 PP composition (test): pass 24.8%, acc 68.7%, tech 6.5%
- top-1000 PP composition (base): pass 25.2%, acc 68.9%, tech 5.9%

maps: 3960; stars change |d| p50 0.399, p90 1.097, max 7.75; corr 0.9759
acc rating corr 0.9441; mean 7.937 vs 8.493; predicted acc mean 0.9830 vs 0.9802
Megametric125 (maps with >=100 scores): mean 0.133 vs 0.135; SD 0.153 vs 0.155; maps >= 0.65: 1.4% vs 1.9%
Megametric (maps with >=100 scores): mean 0.180 vs 0.182; SD 0.167 vs 0.169; maps >= 0.65: 2.3% vs 2.5%
weighted-PP share per map: ratio p5/p50/p95 0.52/1.02/1.90

largest star increases:
                                                                                    Name DifficultyName  Stars_b   Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                                                                                    
2a9e791                                                               Speedcore Paradise     ExpertPlus   11.148  18.896        9.614     16.941           0.978         0.944      13.300       6.694
2dd6cxx92                                                                  Bomb The Rave     ExpertPlus   11.438  16.745       12.534     17.391           0.967         0.942       4.646      11.832
41a23xxxxx91                                                            Unwelcome School     ExpertPlus   15.139  19.902       13.484     17.519           0.961         0.941      11.628      12.970
4324exxxx91                                                           Gravisphere Crisis     ExpertPlus   13.318  16.756       11.977     14.919           0.969         0.955      11.796      10.370
33705xxxxxx91                                                                        999     ExpertPlus   14.469  17.764       11.789     14.545           0.970         0.957      16.697       5.175
3e7d8xx91                                                                         stasis     ExpertPlus   12.543  15.810       11.975     14.792           0.969         0.956       7.939      14.706
1a55e91        My Album Is Out On Dance Corps So Now I Am Allowed To Do A Dancecore Yay!     ExpertPlus    9.085  11.969        8.979     11.735           0.979         0.970       9.484       6.508
3e87191                                                                   Muzik Overload     ExpertPlus   12.371  15.245       11.821     14.266           0.970         0.958       9.928      10.062
338afxx91                                                                          Feral     ExpertPlus   14.095  16.866       12.524     14.765           0.967         0.956      13.152       8.483
1ed491                                                                           Inferno     ExpertPlus   10.740  13.484       10.779     13.220           0.974         0.963       7.671      10.974

largest star decreases:
                                           Name DifficultyName  Stars_b  Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                                          
eb2871                               Holdin' On         Expert    9.563  6.731       11.592      7.906           0.971         0.984       5.144       4.384
3aae492                           Feel The Same     ExpertPlus    8.886  6.163       11.517      7.901           0.971         0.984       3.188       4.400
42a5cxxx72                              epitaxy         Expert    7.899  5.285       11.029      7.463           0.973         0.985       1.673       3.746
3d2a8xxxx92                       Sonic Blaster     ExpertPlus    9.405  6.828       12.188      8.769           0.968         0.981       2.507       5.378
41cfbxxx72    The Intense Voice of Hatsune Miku         Expert    7.565  5.071       10.313      6.890           0.976         0.987       2.094       4.610
3e6cdxxxxx52                             lustre           Hard    8.806  6.356       11.470      8.179           0.971         0.983       2.891       4.739
3ee4bxx72                             RTX 20000         Expert    7.551  5.139       10.289      6.968           0.976         0.987       2.498       3.826
3a6abxxx92     Mystery Circles Ultra / U.U.F.O.     ExpertPlus   10.439  8.042       12.939      9.778           0.965         0.977       3.449       5.829
d4b831                             You Are Mine         Normal   10.374  7.989       12.243      9.119           0.968         0.980       5.390       5.365
4ad7exx92                        Raised by Bats     ExpertPlus    8.344  5.980       11.122      7.908           0.973         0.984       2.703       4.040

base top-500 players moving most (PP change):
                             Name         Pp  Rank       Pp_b  Rank_b      d
Id                                                                          
76561199080950125     blobby56879  26206.432     1  21464.256      12  0.221
76561198110147969       Reezonate  14501.306   904  18006.943     127 -0.195
76561197974131273  JustCallMeJack  21744.572    20  18232.473     108  0.193
76561198186151129   ACC | Pandita  17484.510   240  21649.539      10 -0.192
3022717414503908              amo  21782.912    19  18422.664      91  0.182
76561198308490688         octavia  21993.822    15  18822.672      68  0.168
76561198205546284          m1ddle  19295.805    93  17021.742     268  0.134
76561199115172391         rubbers  22026.729    13  19512.891      41  0.129
76561198255621372  -VGN-ChungusV2  19604.529    81  17457.611     195  0.123
76561199866655859        El Lápiz  16758.016   340  19105.260      58 -0.123
