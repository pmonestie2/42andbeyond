//! Shared world/journal replacement: serialize and sync the temporary file
//! before publishing it with rename. A failed write leaves the old file intact.
use serde::Serialize;
use std::io::{BufWriter, Write};
use std::path::Path;
use std::{fs, io};

/// Publish a fully flushed, synced JSON replacement without changing its durability boundary.
/// Raises:
///     io::Error: Serialization, staging, syncing or atomic rename fails.
pub fn replace<T: Serialize>(path: &Path, value: &T) -> io::Result<()> {
    replace_staged(path, &path.with_extension("json.next"), value)
}

/// Buffer JSON tokens while preserving flush, sync and rename ordering.
/// Raises:
///     io::Error: The complete replacement cannot be serialized or durably published.
pub fn replace_staged<T: Serialize>(path: &Path, temporary: &Path, value: &T) -> io::Result<()> {
    let parent = path
        .parent()
        .ok_or_else(|| io::Error::other("State file needs a parent directory."))?;
    fs::create_dir_all(parent)?;
    // serde_json emits many tiny writes. Sending each token directly to File
    // stalls the authoritative tick under its game lock, delaying all UDP replies.
    let mut output = BufWriter::with_capacity(64 * 1024, fs::File::create(temporary)?);
    serde_json::to_writer(&mut output, value)?;
    // Never rely on BufWriter::drop: it discards flush errors, and syncing before
    // flushing would publish only the last already-drained buffer on disk.
    output.flush()?;
    output.get_ref().sync_all()?;
    drop(output);
    fs::rename(temporary, path)
}

#[cfg(test)]
mod tests {
    use super::*;
    use serde::ser::{SerializeSeq, Serializer};

    struct FailingJson;

    impl Serialize for FailingJson {
        /// Force a failure after enough data to drain the staging buffer.
        fn serialize<S: Serializer>(&self, serializer: S) -> Result<S::Ok, S::Error> {
            let mut sequence = serializer.serialize_seq(None)?;
            sequence.serialize_element(&"staged".repeat(20_000))?;
            Err(serde::ser::Error::custom(
                "deliberate serialization failure",
            ))
        }
    }

    /// Large JSON spans several buffers and retains the final partial buffer after rename.
    #[test]
    fn buffered_replacement_roundtrips_multiple_buffers() {
        let directory =
            std::env::temp_dir().join(format!("42-buffered-json-{}", rand::random::<u64>()));
        fs::create_dir_all(&directory).unwrap();
        let path = directory.join("state.json");
        let expected = serde_json::json!({"actors": (0..2_000).map(|id| serde_json::json!({"id":id,"position":[1.1,2.2,3.3],"name":"a moving sector actor"})).collect::<Vec<_>>(),"tail":"complete"});
        replace(&path, &expected).unwrap();
        let bytes = fs::read(&path).unwrap();
        assert!(bytes.len() > 128 * 1024);
        assert_eq!(
            serde_json::from_slice::<serde_json::Value>(&bytes).unwrap(),
            expected
        );
        assert!(!path.with_extension("json.next").exists());
        fs::remove_dir_all(directory).unwrap();
    }

    /// Serialization failure must leave the previously published state byte-for-byte intact.
    #[test]
    fn partial_serialization_never_publishes_staged_data() {
        let directory =
            std::env::temp_dir().join(format!("42-failed-json-{}", rand::random::<u64>()));
        fs::create_dir_all(&directory).unwrap();
        let path = directory.join("state.json");
        let original = b"{\"published\":true}";
        fs::write(&path, original).unwrap();
        assert!(replace(&path, &FailingJson).is_err());
        assert_eq!(fs::read(&path).unwrap(), original);
        replace(&path, &serde_json::json!({"recovered":true})).unwrap();
        assert_eq!(
            serde_json::from_slice::<serde_json::Value>(&fs::read(&path).unwrap()).unwrap(),
            serde_json::json!({"recovered":true})
        );
        fs::remove_dir_all(directory).unwrap();
    }
}
