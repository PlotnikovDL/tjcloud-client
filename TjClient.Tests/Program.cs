using TjClient.Core;

// Test-only restart routing. Production WPF never recognizes this environment variable.
if (args.Length == 2 && args[0] == "--updater-integration-version") return UpdaterIntegration.VersionContract(args[1]);
if (args.Length == 2 && args[0] == "--updater-integration-guard") return UpdaterIntegration.GuardEntry(args[1]);
if (args.Length == 2 && args[0] == "--updater-integration-server") return await UpdaterIntegration.Server(args[1]);
if (args.Length == 2 && args[0] == "--updater-integration") return await UpdaterIntegration.Client(args[1]);
if (args.Length == 0 && Environment.GetEnvironmentVariable(UpdaterIntegration.ConfigEnvironment) is { Length: > 0 } updaterConfig)
    return await UpdaterIntegration.Client(updaterConfig);

if (args.Length == 2 && args[0] == "--branding-exe") return TjClient.Wpf.BrandingTests.ExecutableIcon(args[1]);
if (args.Length == 2 && args[0] == "--visual-compact") return await TjClient.Wpf.CompactVisuals.Run(args[1]);
if (args.Length == 2 && args[0] == "--protect-config") return IntegrationHarness.Protect(args[1]);
if (args.Length == 2 && args[0] == "--integration") return await IntegrationHarness.Run(args[1]);
if (args.Length == 2 && args[0] == "--unc-scan") return IntegrationHarness.ReadUnc(args[1]);

var tests = new List<(string Name, Action Run)>
{
    ("endpoint_rejects_plaintext_and_secret_urls", () =>
    {
        foreach (var value in new[] { "http://localhost", "https://user:pass@localhost", "https://localhost/?token=x", "https://localhost/#x", "https://localhost/prefix" })
            Assert.Throws<ArgumentException>(() => EndpointPolicy.Parse(value));
        Assert.Equal("https://localhost:8443/", EndpointPolicy.Parse("https://localhost:8443").AbsoluteUri);
    })
};
tests.Add(("test_fixtures_respect_isolated_temporary_root", FixtureTests.IsolatedTemporaryRoot));
tests.Add(("scanner_accepts_only_valid_process_log_paths", ScannerTests.ValidPaths));
tests.Add(("reader_denies_writers_and_reparse_paths", ScannerTests.SafeRead));
tests.Add(("sqlite_queue_persists_baseline_progress_and_quota", QueueTests.Persistence));
tests.Add(("transport_uses_exact_protocol_and_gzip_hash", TransportTests.Protocol));
tests.Add(("transport_classifies_errors_without_response_secrets", TransportTests.Errors));
tests.Add(("engine_streams_file_then_polls_import_without_resending", EngineTests.Delivery));
tests.Add(("engine_resumes_lost_chunk_ack_and_persists_backoff", EngineTests.Resume));
tests.Add(("changed_registered_file_becomes_conflict_without_replacement", EngineTests.ChangedFile));
tests.Add(("profile_requires_bound_consent_and_dpapi_storage", ProfileTests.Protection));
tests.Add(("real_https_rejects_wrong_ca_name_and_redirect", TlsTests.CertificateValidation));
tests.Add(("preview_and_reconfiguration_never_reuse_recipient_secret", ProfileTests.Reconfigure));
tests.Add(("repeated_transient_without_progress_increases_backoff", EngineTests.Backoff));
tests.Add(("delivered_file_changes_and_conflicts_stop_following_files", EngineTests.DeliveredConflict));
tests.Add(("queue_binds_root_origin_source_and_caps_scan", QueueTests.Scope));
tests.Add(("lost_complete_ack_does_not_repeat_import", EngineTests.LostComplete));
tests.Add(("permanent_upload_errors_stop_and_do_not_backoff", EngineTests.PermanentErrors));
tests.Add(("pause_cancels_without_unconfirmed_offset", EngineTests.Cancellation));
tests.Add(("new_files_baseline_is_taken_at_start_not_earlier_preview", QueueTests.ObservationBoundary));
tests.Add(("accepted_upload_never_regresses_to_resend", EngineTests.StateRegression));
tests.Add(("modified_since_covers_start_snapshot_and_late_historical_files", QueueTests.ModifiedSinceBoundary));
tests.Add(("real_https_truncated_body_persists_retry_and_recovers_ack", ResponseBodyTests.TruncatedAcknowledgement));
tests.Add(("real_https_overall_timeout_covers_body_and_preserves_cancellation", ResponseBodyTests.OverallTimeout));

tests.Add(("reconcile_lost_complete_without_original_file", EngineTests.LostCompleteWithoutSource));
tests.Add(("reconcile_accepted_without_root_still_reports_scan_failure", EngineTests.AcceptedWithoutRoot));
tests.Add(("reconcile_uploading_still_requires_available_unchanged_source", EngineTests.UploadingWithoutSource));
tests.Add(("oversize_error_persists_without_blocking_or_automatic_retry", EngineTests.OversizeDoesNotBlock));
tests.Add(("wpf_compact_new_profile_prod_saved_dev_unchanged_no_network", TjClient.Wpf.CompactWindowTests.ProfileDefaults));
tests.Add(("wpf_compact_exclusive_modes_centered_date_locked_period", TjClient.Wpf.CompactWindowTests.ModesAndDate));
tests.Add(("wpf_compact_disclosure_receiver_actions_lifecycle", TjClient.Wpf.CompactWindowTests.CompactSurface));
tests.Add(("wpf_queue_readable_units_exact_bytes_tooltip_fits_column", TjClient.Wpf.QueuePresentationTests.ReadableProgress));
tests.Add(("wpf_queue_actual_text_cells_align_with_headers", TjClient.Wpf.QueuePresentationTests.AlignedCells));
tests.Add(("wpf_branding_tray_uses_custom_icon", TjClient.Wpf.BrandingTests.TrayIcon));
tests.Add(("wpf_branding_icon_lives_until_actual_close", TjClient.Wpf.BrandingTests.IconLifetime));
tests.Add(("wpf_branding_window_uses_multisize_icon", TjClient.Wpf.BrandingTests.WindowIcon));
tests.Add(("wpf_branding_title_shows_assembly_version", TjClient.Wpf.BrandingTests.WindowVersion));
tests.Add(("updater_wpf_real_https_drain_preserves_observation_profile_queue", TjClient.Wpf.UpdateStopTests.RealDrain));
tests.Add(("updater_wpf_dispatcher_exit_guard_preserves_running_or_paused", TjClient.Wpf.UpdateStopTests.DispatcherExitPreservesIntent));
tests.Add(("updater_consent_saved_once_separate_no_network_until_enabled", UpdateConsentTests.ConsentIsSeparateAndOffline));
tests.Add(("updater_consent_revocation_cancels_and_serializes_checks", UpdateConsentTests.RevocationSerializes));
tests.Add(("updater_consent_corruption_offline_failure_never_success", UpdateConsentTests.FailureIsNotSuccess));
tests.Add(("updater_consent_disposal_bounded_without_premature_resource_dispose", UpdateConsentTests.BoundedDispose));
tests.Add(("updater_consent_revocation_detaches_authorization_generation", UpdateConsentTests.RevocationDetachesGeneration));
tests.Add(("updater_ui_consent_checkbox_manual_separate_and_visible", TjClient.Wpf.UpdateUiTests.Surface));
tests.Add(("updater_ui_production_factory_and_smoke_isolation", TjClient.Wpf.UpdateUiTests.ProductionWiring));
tests.Add(("updater_harness01_production_authorization_connected", UpdaterHarnessRegressionTests.AuthorizationConnected));
tests.Add(("updater_harness01_signed_tamper_download_rejected", UpdaterHarnessRegressionTests.TamperRejected));
tests.Add(("updater_handoff01_terminal_before_ready_prevents_close", RuntimeRegressionTests.TerminalBeforeReady));
tests.Add(("updater_handoff01_terminal_before_commit_prevents_close", RuntimeRegressionTests.TerminalBeforeCommit));
tests.Add(("updater_runtime03_shell_free_harmless_actual_argv_and_abort", RuntimeRegressionTests.ShellFreeArguments));
tests.Add(("updater_runtime_revoke_save_failure_explains_restart_limit", RuntimeRegressionTests.RevokeSaveFailure));
tests.Add(("updater_runtime02_start_during_drain_persists_initialized_latest_intent", TjClient.Wpf.UpdateStopTests.StartDuringDrain));
tests.Add(("updater_runtime01_cancellation_during_final_signature_never_launches", SignedUpdateTests.LateCancellation));
tests.Add(("updater_runtime01_close_failure_retains_unconfirmed_handoff", SignedUpdateTests.FailedCloseRetainsOwnership));
tests.Add(("updater_signed_backend_download_drain_verified_launch", SignedUpdateTests.ValidUpdate));
tests.Add(("updater_signed_rejects_invalid_signature_feed_source_os_channel_version", SignedUpdateTests.RejectInvalidUpdates));
tests.Add(("updater_signed_prepare_fail_closed_and_second_signature", SignedUpdateTests.DrainFailuresAndReverification));
tests.Add(("updater_wpf_timeout_abort_keeps_latest_pause_intent", TjClient.Wpf.UpdateStopTests.TimeoutAndPause));
tests.Add(("updater_wpf_exception_aborts_without_persisting_pause", TjClient.Wpf.UpdateStopTests.FailedDrain));
tests.Add(("wpf_check_pause_bounded_exit_and_explicit_oversize_retry", TjClient.Wpf.WindowTests.Cancellation));
var failed = 0;
var selectedTests = tests.Where(t => args.Length == 0 || t.Name.Contains(args[0], StringComparison.Ordinal)).ToList();
foreach (var test in selectedTests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL {test.Name}: {e.GetType().Name}: {e.Message}"); }
}
Console.WriteLine($"RESULT total={selectedTests.Count} failed={failed}");
return failed == 0 && selectedTests.Count > 0 ? 0 : 1;

static class Assert
{
    public static void True(bool value, string message = "Ожидалось true") { if (!value) throw new Exception(message); }
    public static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Ожидалось {expected}, получено {actual}"); }
    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Ожидалось исключение {typeof(T).Name}");
    }
}
