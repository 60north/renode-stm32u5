/* Linker script for STM32U575xI bare-metal test firmware.
 * Memory sizes: stm32u575xx.h (FLASH up to 2 MB; SRAM1/2/3 contiguous from 0x2000 0000,
 * 192 + 64 + 512 KB; SRAM4 16 KB at 0x2800 0000). */

MEMORY
{
    FLASH (rx)  : ORIGIN = 0x08000000, LENGTH = 2048K
    RAM   (rwx) : ORIGIN = 0x20000000, LENGTH = 768K
    SRAM4 (rw)  : ORIGIN = 0x28000000, LENGTH = 16K
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

    /* Not initialised by start-up code. */
    .sram4 (NOLOAD) :
    {
        *(.sram4*)
    } > SRAM4
}
