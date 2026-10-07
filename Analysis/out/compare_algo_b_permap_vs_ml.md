## wwwroot/test-algo-b-permap.db vs wwwroot/test-ml.db

players with PP: 144722 vs 144722; Spearman of player PP 0.99827
- top 10: overlap 5/10; mean PP 22009 vs 22584 (-2.5%)
- top 100: overlap 77/100; mean PP 19802 vs 19667 (+0.7%)
- top 1000: overlap 896/1000; mean PP 16405 vs 16393 (+0.1%)
- base top-1000 players: PP change p5/p50/p95 -6.9% / -0.8% / +6.2%; |rank change| median 98, p90 306, max 684
- base rank 1-100: 100 players, PP change median -0.6% (p10 -4.6%, p90 +4.9%)
- base rank 101-1000: 900 players, PP change median -0.8% (p10 -5.6%, p90 +4.9%)
- base rank 1001-10000: 9000 players, PP change median -3.3% (p10 -6.5%, p90 +2.1%)
- base rank 10001-50000: 40000 players, PP change median -0.3% (p10 -4.3%, p90 +7.1%)
- base rank 50001-end: 94722 players, PP change median +2.9% (p10 -5.8%, p90 +18.9%)
- top-1000 PP composition (test): pass 9.5%, acc 83.5%, tech 7.0%
- top-1000 PP composition (base): pass 25.2%, acc 68.9%, tech 5.9%

maps: 3960; stars change |d| p50 0.825, p90 1.519, max 4.60; corr 0.9724
acc rating corr 0.9420; mean 10.000 vs 8.493; predicted acc mean 0.9828 vs 0.9802
Megametric125 (maps with >=100 scores): mean 0.151 vs 0.135; SD 0.126 vs 0.155; maps >= 0.65: 0.5% vs 1.9%
Megametric (maps with >=100 scores): mean 0.204 vs 0.182; SD 0.139 vs 0.169; maps >= 0.65: 1.1% vs 2.5%
weighted-PP share per map: ratio p5/p50/p95 0.52/1.20/2.68

largest star increases:
                              Name DifficultyName  Stars_b   Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                              
194291                  Chrome Vox     ExpertPlus    9.804  14.404        9.808     16.365           0.977         0.957       7.319      11.282
2a9e791         Speedcore Paradise     ExpertPlus   11.148  15.386        9.614     17.373           0.978         0.951      13.300       6.694
3e7d8xx91                   stasis     ExpertPlus   12.543  16.696       11.975     18.235           0.969         0.945       7.939      14.706
2dd6cxx92            Bomb The Rave     ExpertPlus   11.438  15.140       12.534     17.546           0.967         0.950       4.646      11.832
41f391                   Magnetism     ExpertPlus    9.803  13.483       10.624     15.835           0.975         0.961       5.923       9.684
a7971                 Boku no Pico         Expert    5.476   9.077        6.030     11.124           0.987         0.981       5.715       6.562
6b5f71                       Break         Expert   12.340  15.861       11.829     17.989           0.970         0.947      10.129       9.364
3cf75xxxxxxx91           keep out!     ExpertPlus   10.104  13.492       10.566     15.581           0.975         0.962       6.636      10.410
194271                  Chrome Vox         Expert    7.413  10.451        7.943     12.386           0.982         0.976       6.214       8.394
1e8fe91               Get Get Down     ExpertPlus    5.477   8.465        6.744     10.901           0.985         0.981       4.612       4.848

largest star decreases:
                                           Name DifficultyName  Stars_b   Stars  AccRating_b  AccRating  PredictedAcc_b  PredictedAcc  PassRating  TechRating
Id                                                                                                                                                           
d4b831                             You Are Mine         Normal   10.374   8.101       12.243     10.169           0.968         0.984       5.390       5.365
42a5cxxx72                              epitaxy         Expert    7.899   5.857       11.029      8.353           0.973         0.988       1.673       3.746
4ad7exx92                        Raised by Bats     ExpertPlus    8.344   6.378       11.122      8.745           0.973         0.987       2.703       4.040
3269392                             The Phoenix     ExpertPlus    8.534   6.583       11.501      9.071           0.971         0.987       2.040       4.539
1a58391                      MAXIMUS MACHINEGUN     ExpertPlus   13.236  11.389       11.629     12.727           0.971         0.975      15.343       2.642
3fa34xx92                   UR EMBARRASSING !!!     ExpertPlus    8.105   6.298       10.899      8.622           0.974         0.988       2.181       4.771
46f03xx71                     Calamitous Demise         Expert   12.148  10.353       10.637     11.372           0.974         0.980      14.913       2.845
3aae492                           Feel The Same     ExpertPlus    8.886   7.104       11.517      9.525           0.971         0.985       3.188       4.400
41cfbxxx72    The Intense Voice of Hatsune Miku         Expert    7.565   5.822       10.313      8.040           0.976         0.989       2.094       4.610
3e6cdxxxxx52                             lustre           Hard    8.806   7.078       11.470      9.490           0.971         0.985       2.891       4.739

base top-500 players moving most (PP change):
                                     Name         Pp  Rank       Pp_b  Rank_b      d
Id                                                                                  
76561198433457257                  Oblivy  18531.541   107  16239.147     435  0.141
76561198180044686           CoolingCloset  20295.340    30  17896.303     138  0.134
706                               hampter  15021.464   822  16979.768     278 -0.115
76561198085710824           wobbly shadow  18565.209   103  16653.871     338  0.115
76561199499104226  rootbeermightbethegoat  14933.147   853  16748.736     321 -0.108
76561198138122610         TheJumpingSheep  17793.119   178  16136.002     462  0.103
76561199311713819             -VGN- Shark  14679.684   944  16193.427     448 -0.093
76561199246585600                   bacon  14938.836   850  16447.293     383 -0.092
76561198186151129           ACC | Pandita  19714.355    44  21649.539      10 -0.089
76561199068714821                  axcend  14647.719   952  16057.731     485 -0.088
