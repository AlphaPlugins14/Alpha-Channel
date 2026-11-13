using System.Security.Cryptography;
using System.Text.Json;

namespace AlphaChannel.Plugin.BroadcastAllowance;

internal enum AllowanceStorageOperation { Acquire, Read, Decrypt, Encrypt, Validate, Write, Markers }
internal enum AllowanceStorageFailure { Busy, AccessDenied, Encryption, InvalidRecord, NewerFormat, ChangedExternally, Storage }

internal sealed partial class BroadcastAllowanceService
{
    private FileStream? storageLease;
    private bool storageLoaded, primaryValidated, markersPending;
    private long storageRevision;
    private byte[]? primaryFingerprint;
    private string? retainedPending;
    private DateTime nextStorageRetryUtc;
    private AllowanceStorageFailure storageFailure;

    internal string StorageErrorText => storageFailure switch
    {
        AllowanceStorageFailure.Busy => "Allowance file is in use. Close other game instances; retrying.",
        AllowanceStorageFailure.AccessDenied => "Cannot access allowance storage. Check permissions; retrying.",
        AllowanceStorageFailure.Encryption => "Windows allowance encryption is unavailable. Retrying.",
        AllowanceStorageFailure.NewerFormat => "Allowance file requires a newer plugin. Existing data preserved.",
        AllowanceStorageFailure.InvalidRecord => "Allowance record is invalid. Existing files preserved.",
        AllowanceStorageFailure.ChangedExternally => "Allowance changed elsewhere. Close other instances and reload.",
        _ => "Cannot read or save allowance storage. Retrying automatically.",
    };

    private sealed class NewerAllowanceFormatException : Exception;
    private sealed class AllowanceChangedException : IOException;

    private void ReportStorageFailure(AllowanceStorageOperation operation, Exception exception, bool block = true)
    {
        var failure = exception switch
        {
            NewerAllowanceFormatException => AllowanceStorageFailure.NewerFormat,
            AllowanceChangedException => AllowanceStorageFailure.ChangedExternally,
            UnauthorizedAccessException => AllowanceStorageFailure.AccessDenied,
            CryptographicException => AllowanceStorageFailure.Encryption,
            JsonException or InvalidDataException => AllowanceStorageFailure.InvalidRecord,
            IOException when (exception.HResult & 0xffff) is 32 or 33 => AllowanceStorageFailure.Busy,
            _ => AllowanceStorageFailure.Storage,
        };
        AepLog.AllowanceStorage(operation, failure, exception.HResult);
        if (!block) return;
        storageHealthy = false;
        storageFailure = failure;
        nextStorageRetryUtc = SafeUtcNow().AddSeconds(5);
    }

    private void RetryStorageIfDue()
    {
        if (disposed || SafeUtcNow() < nextStorageRetryUtc) return;
        if (!storageHealthy)
        {
            if (storageLoaded) Save();
            else Restore();
        }
        if (storageHealthy && markersPending)
        {
            nextStorageRetryUtc = SafeUtcNow().AddSeconds(5);
            SynchronizeProtectionMarkers();
        }
    }

    private void Restore()
    {
        var operation = AllowanceStorageOperation.Acquire;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            // A lifetime lease prevents stale snapshots and shared temporary files
            // across processes. Never delete this lock file: another process may
            // already be waiting to open the same filesystem object.
            storageLease ??= new FileStream(statePath + ".lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            operation = AllowanceStorageOperation.Read;
            AllowanceDocument? document = null;
            Exception? invalidRecord = null;
            var mainMissing = false;
            try
            {
                document = ReadDocument(statePath, ref operation, out primaryFingerprint);
                primaryValidated = true;
            }
            catch (FileNotFoundException) { mainMissing = true; }
            catch (Exception exception) when (exception is CryptographicException or JsonException or InvalidDataException)
            {
                invalidRecord = exception;
                // Only data-validation failures qualify for backup recovery.
                AepLog.AllowanceStorage(operation, AllowanceStorageFailure.InvalidRecord, exception.HResult);
            }

            var recoveredCandidate = false;
            {
                var candidates = Directory.GetFiles(Path.GetDirectoryName(statePath)!,
                    Path.GetFileName(statePath) + ".pending-*").AsEnumerable();
                if (document is null) candidates = candidates.Append(statePath + ".bak");
                foreach (var candidate in candidates)
                {
                    try
                    {
                        var recovered = ReadDocument(candidate, ref operation);
                        if (document is null || recovered.Revision > document.Revision)
                        {
                            document = recovered;
                            recoveredCandidate = true;
                        }
                    }
                    catch (FileNotFoundException) { }
                    catch (Exception exception) when (exception is CryptographicException or JsonException or InvalidDataException)
                    {
                        if (exception is CryptographicException || invalidRecord is null) invalidRecord = exception;
                        AepLog.AllowanceStorage(operation, AllowanceStorageFailure.InvalidRecord, exception.HResult);
                    }
                    // Access failures and future formats must not be treated as
                    // permission to discard potentially newer protected usage.
                }
            }

            if (document is null && invalidRecord is CryptographicException)
            {
                // Every available copy failed validation. A pending repair can
                // retry against a fresh ledger balance once storage is usable.
                pendingRepairAccount = null;
                // DPAPI failure can mean a different/unavailable Windows profile,
                // not damaged bytes. Do not overwrite evidence or impose a lock.
                operation = AllowanceStorageOperation.Decrypt;
                throw invalidRecord;
            }

            accounts.Clear();
            if (pendingRepairAccount is { } pending &&
                (document?.Repairs is not { } restored ||
                 !restored.TryGetValue(pending, out var stamp) ||
                 !repairStamps.TryGetValue(pending, out var expected) || stamp != expected))
                pendingRepairAccount = null;
            repairStamps.Clear();
            if (document is not null)
            {
                foreach (var state in document.Accounts) accounts[state.AccountId] = state;
                lockedUntilUtc = document.LockedUntilUtc;
                protectionActivated = document.ProtectionActivated ||
                    accounts.Values.Any(state => state.UsedUnits > 0) || lockedUntilUtc != default;
                storageRevision = document.Revision;
                if (document.Repairs is not null)
                    foreach (var pair in document.Repairs) repairStamps[pair.Key] = pair.Value;
                dirty = recoveredCandidate || !primaryValidated || document.Version != 3;
            }
            else
            {
                // No trustworthy copy remains. Preserve damaged bytes before
                // creating the existing conservative weekly recovery lock.
                protectionActivated = !mainMissing || configuration.BroadcastAllowanceInitialized || hasInitializationMarker();
                lockedUntilUtc = protectionActivated ? StartOfUtcWeek(SafeUtcNow()).AddDays(7) : default;
                dirty = true;
                if (protectionActivated) AepLog.Warning("[Allowance] No valid allowance copy remains; recovery is locked until the next weekly reset.");
            }
            storageLoaded = storageHealthy = true;
            Save();
            if (storageHealthy) SynchronizeProtectionMarkers();
        }
        catch (Exception exception) { ReportStorageFailure(operation, exception); }
    }

    private static AllowanceDocument ReadDocument(string path, ref AllowanceStorageOperation operation) =>
        ReadDocument(path, ref operation, out _);

    private static AllowanceDocument ReadDocument(string path, ref AllowanceStorageOperation operation, out byte[] fingerprint)
    {
        operation = AllowanceStorageOperation.Read;
        // Avoid unbounded allocations on a damaged local record.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > 4 * 1024 * 1024) throw new InvalidDataException();
        var encrypted = new byte[(int)stream.Length];
        stream.ReadExactly(encrypted);
        fingerprint = SHA256.HashData(encrypted);
        operation = AllowanceStorageOperation.Decrypt;
        var plaintext = ProtectedData.Unprotect(encrypted, DpapiEntropy, DataProtectionScope.CurrentUser);
        try
        {
            operation = AllowanceStorageOperation.Validate;
            // Check the format before deserializing fields a future version may change.
            using var json = JsonDocument.Parse(plaintext);
            if (!json.RootElement.TryGetProperty("Version", out var version) || !version.TryGetInt32(out var number))
                throw new InvalidDataException();
            if (number > 3) throw new NewerAllowanceFormatException();
            if (number < 1) throw new InvalidDataException();
            var document = JsonSerializer.Deserialize<AllowanceDocument>(plaintext) ?? throw new InvalidDataException();
            if (document.Accounts is null || document.Revision < 0 || document.Revision == long.MaxValue ||
                document.Repairs?.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null || pair.Value.Version < 1) == true ||
                document.Accounts.Any(state => state is null || string.IsNullOrWhiteSpace(state.AccountId) ||
                    state.AccountId.Length > 128 || state.UsedUnits < 0 || state.UsedUnits > MaximumWeeklyUnits ||
                    state.WeekStartUtc == default || state.LastSeenUtc < state.WeekStartUtc) ||
                document.Accounts.Select(state => state.AccountId).Distinct(StringComparer.Ordinal).Count() != document.Accounts.Length)
                throw new InvalidDataException();
            return document;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private void SaveIfDue()
    {
        if (dirty && SafeUtcNow() - lastSaveUtc >= SaveInterval) Save();
    }

    private void Save()
    {
        if (!dirty || !storageLoaded || storageLease is null ||
            (!storageHealthy && SafeUtcNow() < nextStorageRetryUtc)) return;
        var operation = AllowanceStorageOperation.Write;
        string? temporary = null;
        var completedWrite = false;
        try
        {
            var document = new AllowanceDocument(3, accounts.Values.ToArray(), lockedUntilUtc,
                protectionActivated, checked(storageRevision + 1), new(repairStamps, StringComparer.Ordinal));
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(document);
            byte[] encrypted;
            operation = AllowanceStorageOperation.Encrypt;
            try { encrypted = ProtectedData.Protect(plaintext, DpapiEntropy, DataProtectionScope.CurrentUser); }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            operation = AllowanceStorageOperation.Write;
            if (primaryValidated)
            {
                // Older plugin versions do not take our lease. If they wrote
                // behind it, do not silently overwrite their newer usage.
                using var current = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (primaryFingerprint is null || !SHA256.HashData(current).AsSpan().SequenceEqual(primaryFingerprint))
                    throw new AllowanceChangedException();
            }
            temporary = statePath + ".pending-" + Guid.NewGuid().ToString("N");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                output.Write(encrypted);
                output.Flush(flushToDisk: true);
            }
            completedWrite = true;
            // Validate the completed candidate before changing the last-good copy.
            ReadDocument(temporary, ref operation);
            if (retainedPending is not null)
                try { File.Delete(retainedPending); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            retainedPending = temporary;
            operation = AllowanceStorageOperation.Write;
            if (primaryValidated)
                File.Replace(temporary, statePath, statePath + ".bak", ignoreMetadataErrors: true);
            else
            {
                // Never replace a good backup with the corrupt primary.
                try { File.Copy(statePath, statePath + ".damaged-" + Guid.NewGuid().ToString("N")); }
                catch (FileNotFoundException) { }
                File.Move(temporary, statePath, overwrite: true);
            }
            primaryValidated = true;
            retainedPending = null;
            primaryFingerprint = SHA256.HashData(encrypted);
            storageRevision = document.Revision;
            storageHealthy = true;
            dirty = false;
            lastSaveUtc = SafeUtcNow();
            nextStorageRetryUtc = default;
            try { File.SetAttributes(statePath, File.GetAttributes(statePath) | FileAttributes.Hidden); }
            catch (Exception) { /* The hidden attribute is cosmetic. */ }
            // Only discard interrupted writes after a newer snapshot is committed.
            try
            {
                foreach (var pending in Directory.GetFiles(Path.GetDirectoryName(statePath)!, Path.GetFileName(statePath) + ".pending-*"))
                    try { File.Delete(pending); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        catch (Exception exception) { ReportStorageFailure(operation, exception); }
        finally
        {
            // Keep fully written candidates for crash recovery; remove partial ones.
            if (!completedWrite && temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
