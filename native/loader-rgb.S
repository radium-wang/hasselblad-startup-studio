.text
.p2align 2
.global entry, cleanup
entry:
 stp x0, x30, [sp, #-160]!
 stp x19, x20, [sp, #16]
 stp x21, x22, [sp, #32]
 cbz x25, fallback
 adr x0, mode_path
 mov w1, #0
 bl open_fn
 tbnz w0, #31, fallback
 str w0, [sp, #112]
 add x1, sp, #104
 mov x2, #2
 bl read_fn
 str x0, [sp, #120]
 ldr w0, [sp, #112]
 bl close_fn
 ldr x0, [sp, #120]
 cmp x0, #1
 b.ne fallback
 ldrb w0, [sp, #104]
 cmp w0, #51
 b.ne fallback
 adr x0, image_path
 mov w1, #0
 bl open_fn
 tbnz w0, #31, fallback
 mov w19, w0
 mov x20, #0
 mov x22, #0
header_loop:
 mov w0, w19
 add x1, sp, #64
 add x1, x1, x22
 mov x2, #32
 sub x2, x2, x22
 bl read_fn
 cmp x0, #0
 b.le failure
 add x22, x22, x0
 cmp x22, #32
 b.lo header_loop
 ldr x9, [sp, #64]
 adr x10, magic
 ldr x10, [x10]
 cmp x9, x10
 b.ne failure
 ldr w9, [sp, #72]
 cmp w9, #1
 b.eq format_ok
 cmp w9, #2
 b.ne failure
 cmp w26, #4
 b.ne failure
format_ok:
 ldr w9, [sp, #92]
 cbnz w9, failure
 ldr w9, [sp, #76]
 cbz w9, failure
 cmp w9, #1024
 b.hi failure
 cmp w9, w23
 b.hi failure
 ldr w10, [sp, #80]
 cbz w10, failure
 cmp w10, #768
 b.hi failure
 cmp w10, w24
 b.hi failure
 mul w21, w9, w10
 ldr w9, [sp, #72]
 cmp w9, #2
 b.ne length_ready
 lsl w21, w21, #2
length_ready:
 ldr w9, [sp, #84]
 cmp w21, w9
 b.ne failure
 mov w0, w21
 bl malloc_fn
 cbz x0, failure
 mov x20, x0
 mov x22, #0
pixels_loop:
 mov w0, w19
 add x1, x20, x22
 sub x2, x21, x22
 mov x9, #65536
 cmp x2, x9
 csel x2, x2, x9, lo
 bl read_fn
 cmp x0, #0
 b.le failure
 add x22, x22, x0
 cmp x22, x21
 b.lo pixels_loop
 mov w0, w19
 add x1, sp, #104
 mov x2, #1
 bl read_fn
 cbnz x0, failure
 mov w9, #0x9dc5
 movk w9, #0x811c, lsl #16
 mov w10, #0x0193
 movk w10, #0x0100, lsl #16
 mov x11, x20
 mov x12, x21
checksum_loop:
 ldrb w13, [x11], #1
 eor w9, w9, w13
 mul w9, w9, w10
 subs x12, x12, #1
 b.ne checksum_loop
 ldr w10, [sp, #88]
 cmp w9, w10
 b.ne failure
 mov w0, w19
 bl close_fn
 ldr w9, [sp, #72]
 cmp w9, #2
 b.eq rgb_draw
 adr x19, gray_palette
 ldr w28, [sp, #76]
 ldr w8, [sp, #80]
 ldr x30, [sp, #8]
 add sp, sp, #160
return_draw:
 .inst 0
// BGRX32 is written directly: byte order matches the observed framebuffer.
// Keep our allocation base and the caller's callee-saved registers intact.
rgb_draw:
 cmp w23, #1024
 b.hi rgb_failure
 cmp w24, #768
 b.hi rgb_failure
 ldr w9, [sp, #224]
 lsl w10, w23, #2
 cmp w9, w10
 b.lo rgb_failure
 mov w10, #16384
 cmp w9, w10
 b.hi rgb_failure
 mov x0, x25
 mov w1, #0
 mul w2, w9, w24
 bl memset_fn
 ldr w9, [sp, #76]
 sub w9, w23, w9
 lsr w9, w9, #1
 lsl w9, w9, #2
 ldr w10, [sp, #80]
 sub w10, w24, w10
 lsr w10, w10, #1
 ldr w11, [sp, #224]
 madd w9, w10, w11, w9
 add x19, x25, x9
 mov x22, #0
 mov x11, x20
rgb_row:
 ldr w9, [sp, #76]
 mov x10, x19
rgb_pixel:
 ldr w12, [x11], #4
 and w12, w12, #0x00ffffff
 str w12, [x10], #4
 subs w9, w9, #1
 b.ne rgb_pixel
 ldr w9, [sp, #224]
 add x19, x19, x9
 add x22, x22, #1
 ldr w9, [sp, #80]
 cmp x22, x9
 b.lo rgb_row
 mov x0, x20
 bl free_fn
 ldp x19, x20, [sp, #16]
 ldp x21, x22, [sp, #32]
 ldp x0, x30, [sp], #160
 b return_finish
rgb_failure:
 mov x0, x20
 bl free_fn
 b fallback
failure:
 mov w0, w19
 bl close_fn
 cbz x20, fallback
 mov x0, x20
 bl free_fn
fallback:
 ldp x19, x20, [sp, #16]
 ldp x21, x22, [sp, #32]
 ldp x0, x30, [sp], #160
 cmp w0, #2
 b.eq earth
return_h:
 .inst 0
earth:
return_earth:
 .inst 0
cleanup:
 adr x8, gray_palette
 cmp x19, x8
 b.ne finish
 ldr x8, [sp, #0x38]
 mul x8, x8, x28
 sub x0, x20, x8
 bl free_fn
finish:
return_finish:
 .inst 0
open_fn:
 .inst 0
read_fn:
 .inst 0
close_fn:
 .inst 0
malloc_fn:
 .inst 0
free_fn:
 .inst 0
memset_fn:
 .inst 0
magic:
.ascii "X2DLOGO3"
mode_path:
.asciz "/system/lib64/weston/x2d-startup-mode3"
image_path:
.asciz "/system/lib64/weston/x2d-startup-gray3.bin"
gray_palette:
