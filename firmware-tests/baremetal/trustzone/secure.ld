/* Secure image: secure aliases of the first flash half (veneers in its last 8 KB), SRAM1 and
 * SRAM2, and the lower half of SRAM4 (addresses in trustzone.h). */

MEMORY
{
    FLASH (rx)  : ORIGIN = 0x0C000000, LENGTH = 1016K
    NSC (rx)    : ORIGIN = 0x0C0FE000, LENGTH = 8K
    RAM (rwx)   : ORIGIN = 0x30000000, LENGTH = 256K
    SRAM4 (rw)  : ORIGIN = 0x38000000, LENGTH = 8K
}

ENTRY(Reset_Handler)

_estack = ORIGIN(RAM) + LENGTH(RAM);

SECTIONS
{
    .isr_vector :
    {
        KEEP(*(.isr_vector))
    } > FLASH

    .text :
    {
        *(.text*)
        *(.rodata*)
        . = ALIGN(4);
    } > FLASH

    .gnu.sgstubs :
    {
        . = ALIGN(32);
        *(.gnu.sgstubs*)
        . = ALIGN(32);
    } > NSC

    _sidata = LOADADDR(.data);

    .data :
    {
        _sdata = .;
        *(.data*)
        . = ALIGN(4);
        _edata = .;
    } > RAM AT > FLASH

    .bss (NOLOAD) :
    {
        _sbss = .;
        *(.bss*)
        *(COMMON)
        . = ALIGN(4);
        _ebss = .;
    } > RAM

    .sram4 (NOLOAD) :
    {
        *(.sram4*)
    } > SRAM4
}
