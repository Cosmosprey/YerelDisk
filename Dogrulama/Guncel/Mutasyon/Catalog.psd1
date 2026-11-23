@{
    Mutants = @(
        @{ Id='M10'; Description='Reserve the full user list limit for a tiny input'; File='Core/ObservationAggregate.cs'; Count=1
            From='long>(capacity)'; To='long>(limit)'
            Suite='AccountingOracle'; Expected='Small input with a huge list limit must not reserve the whole limit'; Wpf=$false }
        @{ Id='M01'; Description='Count a hard-link allocation once per name'; File='Core/ObservationAggregate.cs'; Count=1
            From='                    else folder.AllocatedBytes += allocation;'; To='                    folder.AllocatedBytes += allocation;'
            Suite='AccountingOracle'; Expected='Reference allocation mismatch: shared identity'; Wpf=$false }
        @{ Id='M11'; Description='Count a discarded moved observation again'; File='Core/ObservationAggregate.cs'; Count=1
            From='folder.LogicalBytes -= file.Size; folder.FileCount--;'; To='// Mutation: duplicate moved observation counted twice.'
            Suite='AccountingOracle'; Expected='Moved identity must contribute once to logical bytes, count and retained list'; Wpf=$false }
        @{ Id='M02'; Description='Retain smaller rather than larger top-K files'; File='Core/ObservationAggregate.cs'; Count=1
            From='file.Size > floor'; To='file.Size < floor'
            Suite='RefreshExecution'; Expected='Initial winner and complete optional index are available'; Wpf=$false }
        @{ Id='M03'; Description='Accept a sibling with the same path prefix as a descendant'; File='Core/RefreshPlanner.cs'; Count=1
            From="&&(path.Length==n||(path.Length>n&&path[n]=='\\'))"; To=''
            Suite='RefreshPlanner'; Expected='Path prefix sibling is outside the selected scope'; Wpf=$false }
        @{ Id='M04'; Description='Ignore identities shared with outside observations'; File='Core/RefreshPlanner.cs'; Count=1
            From='return Full(RefreshReason.SharedOutside);'; To='return Result(fresh is null ? RefreshAction.MeasureSelected : RefreshAction.MergeSelected, fresh is null ? RefreshReason.SelectedScanRequired : RefreshReason.ReadyToMerge, grant, reserve);'
            Suite='RefreshPlanner'; Expected='Known cross-boundary hard-link identity requests full scope before selected I/O'; Wpf=$false }
        @{ Id='M05'; Description='Forget preparation-token cancellation at publication'; File='Core/RefreshExecution.cs'; Count=2
            From='preparationToken.ThrowIfCancellationRequested();'; To='// Mutation: preparation cancellation forgotten.'
            Suite='RefreshExecution'; Expected='Lease remembers its preparation token and rejects late cancellation with the default validation call'; Wpf=$false }
        @{ Id='M06'; Description='Publish without validating the selected native lease'; File='Application/WorkspaceRemeasure.cs'; Count=1
            From='refreshLease?.ValidateForCommit(source.Token);'; To='source.Token.ThrowIfCancellationRequested(); // Mutation: missing lease validation.'
            Suite='SelectedRefresh'; Expected='Selected lease is validated once after preparation and disposed exactly once'; Wpf=$false }
        @{ Id='M07'; Description='Use editable future paths instead of the old result scope'; File='Application/WorkspaceRemeasure.cs'; Count=1
            From='new ScanRequest(previous.RootPaths, workers, fileLimit, previous.ExcludedPaths, RefreshIndexBudget)'; To='new ScanRequest(Roots.ToArray(), workers, fileLimit, Exclusions.ToArray(), RefreshIndexBudget)'
            Suite='SelectedRefresh'; Expected='Selected request freezes result scope and current metadata preference'; Wpf=$false }
        @{ Id='M08'; Description='Turn a current allocation failure into a larger full scan'; File='Core/RefreshPlanner.cs'; Count=1
            From='if(fresh?.State==RefreshIndexState.AllocationFailed)return Stop(RefreshReason.AllocationFailed);'; To='if(fresh?.State==RefreshIndexState.AllocationFailed)return Full(RefreshReason.MissingIndex);'
            Suite='RefreshPlanner'; Expected='Current fresh allocation failure stops instead of requesting a larger full scan'; Wpf=$false }
        @{ Id='M09'; Description='Drop mixed measurement time in the actual review cell'; File='Wpf/Theme.xaml'; Count=1
            From='<TextBlock.Text><MultiBinding Converter="{StaticResource MeasurementTime}"><Binding /><Binding Path="DataContext" RelativeSource="{RelativeSource AncestorType=DataGrid}" /><Binding Path="DataContext.HasMixedMeasurements" RelativeSource="{RelativeSource AncestorType=DataGrid}" /></MultiBinding></TextBlock.Text>'
            To='<TextBlock.Text><Binding Converter="{StaticResource Cell}" ConverterParameter="Measurement" /></TextBlock.Text>'
            Suite='SelectedRefreshUi'; Expected='Actual rule row identifies mixed parent time without dropping partial or exclusion warnings'; Wpf=$true }
    )
}
