## wwwroot/test-algo-b-blend.db vs wwwroot/test-ml.db

players with PP: 144722 vs 144722; Spearman of player PP 0.99570
- top 10: overlap 5/10; mean PP 21373 vs 22584 (-5.4%)
- top 100: overlap 80/100; mean PP 19408 vs 19667 (-1.3%)
- top 1000: overlap 895/1000; mean PP 16354 vs 16393 (-0.2%)
- base top-1000 players: PP change p5/p50/p95 -6.3% / -1.0% / +5.3%; |rank change| median 89, p90 284, max 653
- base rank 1-100: 100 players, PP change median -2.0% (p10 -6.3%, p90 +2.6%)
- base rank 101-1000: 900 players, PP change median -0.9% (p10 -5.0%, p90 +4.3%)
- base rank 1001-10000: 9000 players, PP change median -3.4% (p10 -8.0%, p90 +4.0%)
- base rank 10001-50000: 40000 players, PP change median -1.3% (p10 -8.1%, p90 +12.9%)
- base rank 50001-end: 94722 players, PP change median +7.3% (p10 -7.6%, p90 +37.0%)
- top-1000 PP composition (test): pass 10.4%, acc 82.8%, tech 6.8%
- top-1000 PP composition (base): pass 25.2%, acc 68.9%, tech 5.9%

maps: 3960; stars change |d| p50 0.984, p90 1.916, max 4.18; corr 0.9719
acc rating corr 0.9464; mean 10.841 vs 8.493; predicted acc mean 0.9828 vs 0.9802
Megametric125 (maps with >=100 scores): mean 0.153 vs 0.135; SD 0.110 vs 0.155; maps >= 0.65: 0.4% vs 1.9%
Megametric (maps with >=100 scores): mean 0.207 vs 0.182; SD 0.117 vs 0.169; maps >= 0.65: 0.6% vs 2.5%
weighted-PP share per map: ratio p5/p50/p95 0.50/1.27/3.05

largest star increases:
                                  Name DifficultyName  Stars_b   Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                                  
194291                      Chrome Vox     ExpertPlus    9.804  13.984        9.808     16.530           0.977         0.957       7.319      11.282
2a9e791             Speedcore Paradise     ExpertPlus   11.148  15.015        9.614     17.388           0.978         0.951      13.300       6.694
a7971                     Boku no Pico         Expert    5.476   9.179        6.030     11.926           0.987         0.981       5.715       6.562
3e7d8xx91                       stasis     ExpertPlus   12.543  16.006       11.975     18.114           0.969         0.945       7.939      14.706
41f391                       Magnetism     ExpertPlus    9.803  13.153       10.624     16.076           0.975         0.961       5.923       9.684
2812fxx91       Halo Reach - Lone Wolf     ExpertPlus    3.957   7.165        6.062     10.564           0.987         0.985       1.762       3.025
2dd6cxx92                Bomb The Rave     ExpertPlus   11.438  14.611       12.534     17.534           0.967         0.950       4.646      11.832
1e8fe91                   Get Get Down     ExpertPlus    5.477   8.625        6.744     11.723           0.985         0.981       4.612       4.848
4adb31            Deja Vu (Short Ver.)         Normal    2.891   6.025        4.916      9.275           0.990         0.989       1.008       2.462
3cf75xxxxxxx91               keep out!     ExpertPlus   10.104  13.178       10.566     15.857           0.975         0.962       6.636      10.410

largest star decreases:
                            Name DifficultyName  Stars_b   Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                            
d4b831              You Are Mine         Normal   10.374   8.254       12.243     11.053           0.968         0.984       5.390       5.365
1a58391       MAXIMUS MACHINEGUN     ExpertPlus   13.236  11.618       11.629     13.363           0.971         0.975      15.343       2.642
4ad7exx92         Raised by Bats     ExpertPlus    8.344   6.730       11.122      9.729           0.973         0.987       2.703       4.040
42a5cxxx72               epitaxy         Expert    7.899   6.317       11.029      9.360           0.973         0.988       1.673       3.746
46f03xx71      Calamitous Demise         Expert   12.148  10.579       10.637     12.150           0.974         0.980      14.913       2.845
3269392              The Phoenix     ExpertPlus    8.534   6.988       11.501     10.035           0.971         0.987       2.040       4.539
3aae492            Feel The Same     ExpertPlus    8.886   7.401       11.517     10.458           0.971         0.985       3.188       4.400
449dexxx91        Three Bastards     ExpertPlus   12.369  10.925       11.170     12.820           0.973         0.977      14.422       2.516
3e6cdxxxxx72              lustre         Expert   10.509   9.084       12.900     12.134           0.965         0.980       3.473       6.426
3e6cdxxxxx52              lustre           Hard    8.806   7.398       11.470     10.426           0.971         0.985       2.891       4.739

base top-500 players moving most (PP change):
                                     Name         Pp  Rank       Pp_b  Rank_b      d
Id                                                                                  
76561198186151129           ACC | Pandita  19092.982    54  21649.539      10 -0.118
76561198433457257                  Oblivy  18130.809   117  16239.147     435  0.116
76561198180044686           CoolingCloset  19781.738    31  17896.303     138  0.105
76561198085710824           wobbly shadow  18320.121   104  16653.871     338  0.100
76561198027277296                    Kira  20066.580    23  22254.826       6 -0.098
706                               hampter  15312.534   733  16979.768     278 -0.098
76561198839026440                   Haste  20098.062    21  22268.012       5 -0.097
76561199499104226  rootbeermightbethegoat  15224.628   766  16748.736     321 -0.091
3225556157461414                 Bizzy825  22020.129     2  24141.768       1 -0.088
76561198138122610         TheJumpingSheep  17551.096   189  16136.002     462  0.088
