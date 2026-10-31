/* Non-secure image: second flash half, SRAM3 without its last 16 KB super-block (secure), and
 * the upper half of SRAM4 (addresses in trustzone.h). */

MEMORY
{
    FLASH (rx)  : ORIGIN = 0x08100000, LENGTH = 1024K
    RAM (rwx)   : ORIGIN = 0x20040000, LENGTH = 496K
    SRAM4 (rw)  : ORIGIN = 0x28002000, LENGTH = 8K
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
