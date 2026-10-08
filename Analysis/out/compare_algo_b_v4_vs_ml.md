## wwwroot/test-algo-b-v4.db vs wwwroot/test-ml.db

players with PP: 144722 vs 144722; Spearman of player PP 0.99636
- top 10: overlap 5/10; mean PP 21564 vs 22584 (-4.5%)
- top 100: overlap 80/100; mean PP 19572 vs 19667 (-0.5%)
- top 1000: overlap 893/1000; mean PP 16572 vs 16393 (+1.1%)
- base top-1000 players: PP change p5/p50/p95 -5.3% / +0.5% / +6.8%; |rank change| median 100, p90 301, max 663
- base rank 1-100: 100 players, PP change median -1.3% (p10 -5.7%, p90 +3.9%)
- base rank 101-1000: 900 players, PP change median +0.6% (p10 -3.7%, p90 +5.6%)
- base rank 1001-10000: 9000 players, PP change median -1.4% (p10 -6.4%, p90 +5.6%)
- base rank 10001-50000: 40000 players, PP change median +0.2% (p10 -6.9%, p90 +13.7%)
- base rank 50001-end: 94722 players, PP change median +8.4% (p10 -6.4%, p90 +34.0%)
- top-1000 PP composition (test): pass 11.1%, acc 81.9%, tech 7.1%
- top-1000 PP composition (base): pass 25.2%, acc 68.9%, tech 5.9%

maps: 3960; stars change |d| p50 0.972, p90 1.885, max 4.39; corr 0.9755
acc rating corr 0.9587; mean 10.824 vs 8.493; predicted acc mean 0.9829 vs 0.9802
Megametric125 (maps with >=100 scores): mean 0.141 vs 0.135; SD 0.130 vs 0.155; maps >= 0.65: 1.1% vs 1.9%
Megametric (maps with >=100 scores): mean 0.191 vs 0.182; SD 0.139 vs 0.169; maps >= 0.65: 1.8% vs 2.5%
weighted-PP share per map: ratio p5/p50/p95 0.45/1.17/2.82

largest star increases:
                         Name DifficultyName  Stars_b   Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                         
2a9e791    Speedcore Paradise     ExpertPlus   11.148  15.536        9.614     17.883           0.978         0.947      13.783       6.694
2dd6cxx92       Bomb The Rave     ExpertPlus   11.438  15.712       12.534     18.130           0.967         0.945       9.190      11.832
63671                    Burn         Expert    5.574   8.879        7.297     11.811           0.984         0.981       2.983       7.099
5b671          No, Thank you!         Expert    5.381   8.437        6.669     11.243           0.985         0.983       3.888       6.679
3e7d8xx91              stasis     ExpertPlus   12.543  15.533       11.975     17.038           0.969         0.954      10.666      14.706
a7971            Boku no Pico         Expert    5.476   8.430        6.030     11.042           0.987         0.984       5.440       6.562
9d071               Fake Love         Expert    6.518   9.420        7.692     11.980           0.983         0.980       3.738       9.253
15571           Midnight City         Expert    6.060   8.926        7.522     11.829           0.983         0.981       3.691       6.888
194291             Chrome Vox     ExpertPlus    9.804  12.641        9.808     14.833           0.977         0.968       7.943      11.282
15591           Midnight City     ExpertPlus    7.680  10.425        8.372     12.688           0.981         0.978       5.217      10.921

largest star decreases:
                                                              Name DifficultyName  Stars_b   Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                                                              
1a58391                                         MAXIMUS MACHINEGUN     ExpertPlus   13.236  11.161       11.629     13.691           0.971         0.973      12.450       2.642
1d0ed91                                         The Imperial March     ExpertPlus   10.834   9.066       10.889     11.796           0.974         0.981       7.689       4.710
30f6axxx91                                      sprinter/intercity     ExpertPlus   10.688   9.281       10.481     12.109           0.975         0.980       9.660       2.274
eb2871                                                  Holdin' On         Expert    9.563   8.168       11.592     10.987           0.971         0.984       6.269       4.384
1492f71                                                  C18H27NO3         Expert    9.486   8.129       11.142     11.247           0.973         0.983       6.503       2.508
205d2x91                                      Multigenre Smackdown     ExpertPlus   12.411  11.060       11.294     13.127           0.972         0.976      11.808       5.840
2a45a91                                            Scattered Faith     ExpertPlus   12.307  10.959       11.234     13.230           0.972         0.975      12.482       3.686
1bb8d71                                               Metamorphose         Expert   10.199   8.871       10.419     11.856           0.975         0.981       7.126       3.649
d4b831                                                You Are Mine         Normal   10.374   9.087       12.243     11.916           0.968         0.981       6.476       5.365
13b2b51     Extra Credit on the Chromosome Test! VICTORY ROYALE!!!           Hard   11.014   9.790       11.262     12.498           0.972         0.978       7.224       6.190

base top-500 players moving most (PP change):
                                     Name         Pp  Rank       Pp_b  Rank_b      d
Id                                                                                  
76561198186151129           ACC | Pandita  18980.039    65  21649.539      10 -0.123
76561198433457257                  Oblivy  18114.971   139  16239.147     435  0.116
76561198085710824           wobbly shadow  18536.703   102  16653.871     338  0.113
76561198296328455          Contradictions  17957.014   154  16326.317     413  0.100
76561198027277296                    Kira  20079.221    27  22254.826       6 -0.098
76561198004960260            rockothetaco  17762.250   182  16229.469     437  0.094
76561198180044686           CoolingCloset  19586.021    39  17896.303     138  0.094
76561198839026440                   Haste  20166.727    23  22268.012       5 -0.094
76561199499104226  rootbeermightbethegoat  15175.018   870  16748.736     321 -0.094
76561199071133731                 lotus ❀  17773.758   181  16327.896     412  0.089
