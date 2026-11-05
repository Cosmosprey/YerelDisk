@{
    Suites = @(
        @{ Name='Inventory'; Group='Normal'; Assembly='YerelDisk.Application.Tests'; MinimumPass=61; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='InventoryUi'; Group='Normal'; Assembly='YerelDisk.Wpf.Tests'; MinimumPass=27; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='Filtering'; Group='Normal'; Assembly='YerelDisk.Application.Tests'; MinimumPass=22; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='FilteringUi'; Group='Normal'; Assembly='YerelDisk.Wpf.Tests'; MinimumPass=27; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='ClassificationCorpus'; Group='Normal'; Assembly='YerelDisk.Application.Tests'; MinimumPass=44; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='OtherAreas'; Group='Normal'; Assembly='YerelDisk.Application.Tests'; MinimumPass=40; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='OtherAreasUi'; Group='Normal'; Assembly='YerelDisk.Wpf.Tests'; MinimumPass=30; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='ReviewGuidance'; Group='Normal'; Assembly='YerelDisk.Application.Tests'; MinimumPass=31; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='ReviewTable'; Group='Normal'; Assembly='YerelDisk.Wpf.Tests'; MinimumPass=58; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='ReviewMetadata'; Group='Normal'; Assembly='YerelDisk.Application.Tests'; MinimumPass=55; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='ReviewMetadataUi'; Group='Normal'; Assembly='YerelDisk.Wpf.Tests'; MinimumPass=26; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='ReviewMetadataNative'; Group='Native'; Assembly='YerelDisk.Application.Tests'; MinimumPass=6; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@('HardLink','Junction','Acl') }
        @{ Name='Distribution'; Group='Normal'; Assembly='YerelDisk.Application.Tests'; MinimumPass=43; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='DistributionUi'; Group='Normal'; Assembly='YerelDisk.Wpf.Tests'; MinimumPass=28; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='Exclusions'; Group='Normal'; Assembly='YerelDisk.Application.Tests'; MinimumPass=61; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='ExclusionsUi'; Group='Normal'; Assembly='YerelDisk.Wpf.Tests'; MinimumPass=26; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='ExclusionsNative'; Group='Native'; Assembly='YerelDisk.Application.Tests'; MinimumPass=9; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@('HardLink','Junction','Acl') }
        @{ Name='Application'; Group='Normal'; Assembly='YerelDisk.Application.Tests'; MinimumPass=55; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='Summary'; Group='Normal'; Assembly='YerelDisk.Application.Tests'; MinimumPass=41; ResultKind='Directory'; Success='^SUMMARY CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='IdentityCapacity'; Group='Normal'; Assembly='YerelDisk.Application.Tests'; MinimumPass=10; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='Review'; Group='Normal'; Assembly='YerelDisk.Application.Tests'; MinimumPass=139; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='ReviewUi'; Group='Normal'; Assembly='YerelDisk.Wpf.Tests'; MinimumPass=19; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='SummaryUi'; Group='Normal'; Assembly='YerelDisk.Wpf.Tests'; MinimumPass=23; ResultKind='Directory'; Success='^ALL CHECKS PASSED \(\d+\)$'; Capabilities=@() }
        @{ Name='Movement'; Group='Native'; Assembly='YerelDisk.Application.Tests'; MinimumPass=16; ResultKind='Directory'; Success='^RESULT PASS=\d+; Failures=0$'; Capabilities=@('HardLink') }
        @{ Name='Recovery'; Group='Native'; Assembly='YerelDisk.Application.Tests'; MinimumPass=57; ResultKind='Directory'; Success='^RESULT PASS=\d+; Failures=0$'; Capabilities=@('HardLink','Junction','Acl') }
        @{ Name='Regression'; Group='Native'; Assembly='YerelDisk.Native.Regression'; MinimumPass=17; ResultKind='CoreAndLog'; Success='^Failures=0$'; Capabilities=@('HardLink','Junction','Streams') }
        @{ Name='Errors'; Group='Native'; Assembly='YerelDisk.Native.Errors'; MinimumPass=12; ResultKind='CoreLogAndFixtures'; Success='^Failures=0$'; Capabilities=@('Acl') }
    )
}
