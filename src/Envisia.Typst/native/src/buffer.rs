//! Memory the managed side fills once, typically from a stream, and then hands to any number of compilations
//! without it being copied again.
//!
//! A buffer has two states. While it is being filled the managed side is its only user and calls it from one thread
//! at a time: it reserves spare capacity, writes into it and commits what it wrote. Sealing freezes it. From then on its bytes never change, concurrent
//! compilations can share it, and it lives until the managed side has released its handle and Typst has dropped
//! every `Bytes` that points into it, including the ones comemo's cache still holds.
//!
//! The handle is an `Arc<NativeBuffer>` turned into a raw pointer; each handle owns one strong reference. Like
//! `envisia_typst_compile_pdf`, every call reports a panic as a status instead of letting it abort the process.

use std::mem::ManuallyDrop;
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::ptr;
use std::sync::Arc;

use typst::foundations::Bytes;

use crate::{STATUS_INVALID_INPUT, STATUS_OK, STATUS_OUT_OF_MEMORY, STATUS_PANIC};

pub struct NativeBuffer {
    data: Vec<u8>,
    sealed: bool,
}

struct SharedSlice {
    buffer: Arc<NativeBuffer>,
    start: usize,
    len: usize,
}

impl AsRef<[u8]> for SharedSlice {
    fn as_ref(&self) -> &[u8] {
        &self.buffer.data[self.start..self.start + self.len]
    }
}

/// Creates an empty buffer that can take `capacity` bytes before it has to grow. Returns null when the memory is not
/// available.
#[no_mangle]
pub extern "C" fn envisia_typst_buffer_new(capacity: usize) -> *const NativeBuffer {
    catch_unwind(|| {
        let mut data = Vec::new();
        if data.try_reserve_exact(capacity).is_err() {
            return ptr::null();
        }

        Arc::into_raw(Arc::new(NativeBuffer {
            data,
            sealed: false,
        }))
    })
    .unwrap_or(ptr::null())
}

/// Makes sure at least `additional` bytes of spare capacity follow the committed bytes and reports all of it. The
/// caller may write into `*spare .. *spare + *spare_len` and then commits how much it wrote; any other call on the
/// buffer invalidates the pointer.
///
/// # Safety
/// `buffer` must be a live handle from `envisia_typst_buffer_new` that no other call uses at the same time; `spare`
/// and `spare_len` must be writable.
#[no_mangle]
pub unsafe extern "C" fn envisia_typst_buffer_reserve(
    buffer: *const NativeBuffer,
    additional: usize,
    spare: *mut *mut u8,
    spare_len: *mut usize,
) -> i32 {
    guarded(|| {
        let Some(buffer) = filling(buffer) else {
            return STATUS_INVALID_INPUT;
        };
        if spare.is_null() || spare_len.is_null() {
            return STATUS_INVALID_INPUT;
        }

        if buffer.data.try_reserve(additional).is_err() {
            return STATUS_OUT_OF_MEMORY;
        }

        let region = buffer.data.spare_capacity_mut();
        *spare = region.as_mut_ptr().cast();
        *spare_len = region.len();
        STATUS_OK
    })
}

/// Appends the first `count` bytes of the spare capacity, which the caller has written.
///
/// # Safety
/// `buffer` must be a live handle from `envisia_typst_buffer_new` that no other call uses at the same time, and the
/// caller must have written `count` bytes at the start of the region the last `envisia_typst_buffer_reserve`
/// reported.
#[no_mangle]
pub unsafe extern "C" fn envisia_typst_buffer_commit(
    buffer: *const NativeBuffer,
    count: usize,
) -> i32 {
    guarded(|| {
        let Some(buffer) = filling(buffer) else {
            return STATUS_INVALID_INPUT;
        };
        if count > buffer.data.capacity() - buffer.data.len() {
            return STATUS_INVALID_INPUT;
        }

        buffer.data.set_len(buffer.data.len() + count);
        STATUS_OK
    })
}

/// Freezes the buffer, returns the spare capacity and reports where its bytes are. A sealed buffer can no longer be
/// written, only shared.
///
/// # Safety
/// `buffer` must be a live handle from `envisia_typst_buffer_new` that no other call uses at the same time; `data` and
/// `len` must be writable.
#[no_mangle]
pub unsafe extern "C" fn envisia_typst_buffer_seal(
    buffer: *const NativeBuffer,
    data: *mut *const u8,
    len: *mut usize,
) -> i32 {
    guarded(|| {
        let Some(buffer) = filling(buffer) else {
            return STATUS_INVALID_INPUT;
        };
        if data.is_null() || len.is_null() {
            return STATUS_INVALID_INPUT;
        }

        buffer.data.shrink_to_fit();
        buffer.sealed = true;
        *data = buffer.data.as_ptr();
        *len = buffer.data.len();
        STATUS_OK
    })
}

/// Gives up the handle's reference. The bytes stay alive for as long as a compilation still uses them.
///
/// # Safety
/// `buffer` must be null or a live handle from `envisia_typst_buffer_new`, and must not be used afterwards.
#[no_mangle]
pub unsafe extern "C" fn envisia_typst_buffer_release(buffer: *const NativeBuffer) {
    if !buffer.is_null() {
        let _ = catch_unwind(|| drop(Arc::from_raw(buffer)));
    }
}

/// Hands `len` bytes at `data` inside the sealed buffer to Typst without copying them. The returned `Bytes` holds a
/// reference of its own.
///
/// # Safety
/// `buffer` must be a live handle from `envisia_typst_buffer_new`.
pub unsafe fn share(
    buffer: *const NativeBuffer,
    data: *const u8,
    len: usize,
) -> Result<Bytes, String> {
    let buffer = ManuallyDrop::new(Arc::from_raw(buffer));
    if !buffer.sealed {
        return Err("a buffer was handed in before it was sealed".to_owned());
    }

    let start = (data as usize)
        .checked_sub(buffer.data.as_ptr() as usize)
        .filter(|start| {
            start
                .checked_add(len)
                .is_some_and(|end| end <= buffer.data.len())
        })
        .ok_or_else(|| "a buffer slice lies outside of its buffer".to_owned())?;

    Ok(Bytes::new(SharedSlice {
        buffer: Arc::clone(&buffer),
        start,
        len,
    }))
}

fn guarded(call: impl FnOnce() -> i32) -> i32 {
    catch_unwind(AssertUnwindSafe(call)).unwrap_or(STATUS_PANIC)
}

/// The buffer behind `handle` while it is being filled: not sealed and not shared with anything. The reference count
/// only proves that nothing else holds the buffer; that no two calls fill it at once is up to the caller.
unsafe fn filling<'a>(handle: *const NativeBuffer) -> Option<&'a mut NativeBuffer> {
    if handle.is_null() {
        return None;
    }

    let mut arc = ManuallyDrop::new(Arc::from_raw(handle));
    let buffer: *mut NativeBuffer = Arc::get_mut(&mut arc)?;
    let buffer = &mut *buffer;
    (!buffer.sealed).then_some(buffer)
}

#[cfg(test)]
mod tests {
    use super::*;

    unsafe fn fill(buffer: *const NativeBuffer, bytes: &[u8]) {
        let mut spare = ptr::null_mut();
        let mut spare_len = 0;
        assert_eq!(
            envisia_typst_buffer_reserve(buffer, bytes.len(), &mut spare, &mut spare_len),
            STATUS_OK
        );
        assert!(spare_len >= bytes.len());
        ptr::copy_nonoverlapping(bytes.as_ptr(), spare, bytes.len());
        assert_eq!(envisia_typst_buffer_commit(buffer, bytes.len()), STATUS_OK);
    }

    unsafe fn seal(buffer: *const NativeBuffer) -> (*const u8, usize) {
        let mut data = ptr::null();
        let mut len = 0;
        assert_eq!(
            envisia_typst_buffer_seal(buffer, &mut data, &mut len),
            STATUS_OK
        );
        (data, len)
    }

    #[test]
    fn grows_past_its_initial_capacity_and_shares_what_was_committed() {
        unsafe {
            let buffer = envisia_typst_buffer_new(2);
            fill(buffer, b"Hallo ");
            fill(buffer, b"Welt");
            let (data, len) = seal(buffer);

            assert_eq!(len, 10);
            let bytes = share(buffer, data.add(6), 4).unwrap();
            assert_eq!(bytes.as_slice(), b"Welt");
            envisia_typst_buffer_release(buffer);
        }
    }

    #[test]
    fn shared_bytes_outlive_the_released_handle() {
        unsafe {
            let buffer = envisia_typst_buffer_new(0);
            fill(buffer, b"bleibt");
            let (data, len) = seal(buffer);
            let bytes = share(buffer, data, len).unwrap();

            envisia_typst_buffer_release(buffer);

            assert_eq!(bytes.as_slice(), b"bleibt");
        }
    }

    #[test]
    fn an_empty_buffer_shares_an_empty_slice() {
        unsafe {
            let buffer = envisia_typst_buffer_new(0);
            let (data, len) = seal(buffer);

            assert_eq!(len, 0);
            assert!(share(buffer, data, 0).unwrap().is_empty());
            envisia_typst_buffer_release(buffer);
        }
    }

    #[test]
    fn a_sealed_buffer_can_no_longer_be_written() {
        unsafe {
            let buffer = envisia_typst_buffer_new(8);
            fill(buffer, b"fest");
            seal(buffer);

            let mut spare = ptr::null_mut();
            let mut spare_len = 0;
            assert_eq!(
                envisia_typst_buffer_reserve(buffer, 1, &mut spare, &mut spare_len),
                STATUS_INVALID_INPUT
            );
            assert_eq!(envisia_typst_buffer_commit(buffer, 0), STATUS_INVALID_INPUT);
            let mut data = ptr::null();
            let mut len = 0;
            assert_eq!(
                envisia_typst_buffer_seal(buffer, &mut data, &mut len),
                STATUS_INVALID_INPUT
            );
            envisia_typst_buffer_release(buffer);
        }
    }

    #[test]
    fn commits_no_more_than_the_spare_capacity() {
        unsafe {
            let buffer = envisia_typst_buffer_new(4);
            let mut spare = ptr::null_mut();
            let mut spare_len = 0;
            envisia_typst_buffer_reserve(buffer, 4, &mut spare, &mut spare_len);

            assert_eq!(
                envisia_typst_buffer_commit(buffer, spare_len + 1),
                STATUS_INVALID_INPUT
            );
            envisia_typst_buffer_release(buffer);
        }
    }

    #[test]
    fn refuses_to_share_an_unsealed_buffer() {
        unsafe {
            let buffer = envisia_typst_buffer_new(4);
            fill(buffer, b"offen");

            assert!(share(buffer, ptr::null(), 0).is_err());
            envisia_typst_buffer_release(buffer);
        }
    }

    #[test]
    fn refuses_a_slice_outside_of_the_buffer() {
        unsafe {
            let buffer = envisia_typst_buffer_new(0);
            fill(buffer, b"kurz");
            let (data, len) = seal(buffer);

            assert!(share(buffer, data.add(1), len).is_err());
            assert!(share(buffer, data.wrapping_sub(1), 1).is_err());
            assert!(share(buffer, ptr::null(), 0).is_err());
            envisia_typst_buffer_release(buffer);
        }
    }

    #[test]
    fn reports_memory_that_is_not_available() {
        assert!(envisia_typst_buffer_new(usize::MAX).is_null());
    }
}
