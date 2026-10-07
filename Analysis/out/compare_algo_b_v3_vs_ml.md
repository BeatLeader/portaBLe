## wwwroot/test-algo-b-v3.db vs wwwroot/test-ml.db

players with PP: 144722 vs 144722; Spearman of player PP 0.99648
- top 10: overlap 5/10; mean PP 21286 vs 22584 (-5.7%)
- top 100: overlap 84/100; mean PP 19413 vs 19667 (-1.3%)
- top 1000: overlap 896/1000; mean PP 16462 vs 16393 (+0.4%)
- base top-1000 players: PP change p5/p50/p95 -5.1% / -0.3% / +5.9%; |rank change| median 87, p90 286, max 591
- base rank 1-100: 100 players, PP change median -1.7% (p10 -5.8%, p90 +2.1%)
- base rank 101-1000: 900 players, PP change median -0.1% (p10 -3.9%, p90 +4.6%)
- base rank 1001-10000: 9000 players, PP change median -2.0% (p10 -6.7%, p90 +5.3%)
- base rank 10001-50000: 40000 players, PP change median +0.2% (p10 -6.8%, p90 +14.0%)
- base rank 50001-end: 94722 players, PP change median +8.4% (p10 -5.7%, p90 +34.8%)
- top-1000 PP composition (test): pass 10.3%, acc 82.9%, tech 6.8%
- top-1000 PP composition (base): pass 25.2%, acc 68.9%, tech 5.9%

maps: 3960; stars change |d| p50 0.927, p90 1.877, max 4.09; corr 0.9798
acc rating corr 0.9605; mean 10.830 vs 8.493; predicted acc mean 0.9829 vs 0.9802
Megametric125 (maps with >=100 scores): mean 0.140 vs 0.135; SD 0.129 vs 0.155; maps >= 0.65: 1.0% vs 1.9%
Megametric (maps with >=100 scores): mean 0.190 vs 0.182; SD 0.138 vs 0.169; maps >= 0.65: 1.5% vs 2.5%
weighted-PP share per map: ratio p5/p50/p95 0.51/1.14/2.66

largest star increases:
                         Name DifficultyName  Stars_b   Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                         
2a9e791    Speedcore Paradise     ExpertPlus   11.148  15.237        9.614     17.656           0.978         0.949      13.300       6.694
2dd6cxx92       Bomb The Rave     ExpertPlus   11.438  15.438       12.534     18.505           0.967         0.941       4.646      11.832
63671                    Burn         Expert    5.574   8.968        7.297     11.987           0.984         0.980       2.317       7.099
15571           Midnight City         Expert    6.060   9.202        7.522     12.185           0.983         0.980       3.521       6.888
9d071               Fake Love         Expert    6.518   9.623        7.692     12.257           0.983         0.979       3.470       9.253
5b671          No, Thank you!         Expert    5.381   8.424        6.669     11.266           0.985         0.983       3.563       6.679
15591           Midnight City     ExpertPlus    7.680  10.673        8.372     13.054           0.981         0.976       4.697      10.921
a7971            Boku no Pico         Expert    5.476   8.462        6.030     11.039           0.987         0.984       5.715       6.562
9d051               Fake Love           Hard    4.504   7.275        6.509     10.374           0.986         0.986       1.724       4.818
243d51            Flat Zone 2           Hard    2.822   5.513        4.813      8.667           0.990         0.990       1.175       2.068

largest star decreases:
                                                            Name DifficultyName  Stars_b   Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                                                            
eb2871                                                Holdin' On         Expert    9.563   8.194       11.592     11.198           0.971         0.983       5.144       4.384
1492f71                                                C18H27NO3         Expert    9.486   8.187       11.142     11.230           0.973         0.983       7.010       2.508
1a58391                                       MAXIMUS MACHINEGUN     ExpertPlus   13.236  11.956       11.629     13.790           0.971         0.973      15.343       2.642
d4b831                                              You Are Mine         Normal   10.374   9.117       12.243     12.124           0.968         0.980       5.390       5.365
1d0ed91                                       The Imperial March     ExpertPlus   10.834   9.599       10.889     11.869           0.974         0.981      10.435       4.710
20ad7x91                                                    MORE     ExpertPlus   11.475  10.288       11.696     12.976           0.971         0.977      11.129       2.505
1492f91                                                C18H27NO3     ExpertPlus   11.281  10.140       12.730     13.366           0.966         0.975       7.665       3.474
2a45a91                                          Scattered Faith     ExpertPlus   12.307  11.187       11.234     13.157           0.972         0.976      13.701       3.686
205d2x91                                    Multigenre Smackdown     ExpertPlus   12.411  11.307       11.294     13.113           0.972         0.976      12.973       5.840
13b2b91   Extra Credit on the Chromosome Test! VICTORY ROYALE!!!     ExpertPlus   12.830  11.744       12.159     14.063           0.969         0.972      12.803       3.968

base top-500 players moving most (PP change):
                            Name         Pp  Rank       Pp_b  Rank_b      d
Id                                                                         
76561198433457257         Oblivy  18226.820   113  16239.147     435  0.122
76561198186151129  ACC | Pandita  19065.525    53  21649.539      10 -0.119
76561198027277296           Kira  20005.061    26  22254.826       6 -0.101
3225556157461414        Bizzy825  21818.467     2  24141.768       1 -0.096
76561198085710824  wobbly shadow  18216.609   115  16653.871     338  0.094
76561198839026440          Haste  20269.008    17  22268.012       5 -0.090
76561198004960260   rockothetaco  17637.029   183  16229.469     437  0.087
706                      hampter  15519.039   713  16979.768     278 -0.086
76561198118728813           Karu  17679.445   175  16342.941     410  0.082
76561198180044686  CoolingCloset  19346.812    43  17896.303     138  0.081
