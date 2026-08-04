silent
printf "\n[chain-hit] rip=%p rsp=%p rbp=%p rbx=%p rsi=%p rdi=%p r14=%p r15=%p\n", $rip,$rsp,$rbp,$rbx,$rsi,$rdi,$r14,$r15
printf "rax=%p rcx=%p rdx=%p r8=%p r9=%p r10=%p r11=%p\n", $rax,$rcx,$rdx,$r8,$r9,$r10,$r11
x/i $rip
if $rbx == 0 && $rsi == 0 && $rdi == 0
  printf "\n[chain-first-zero] nonvolatiles are zero at this return point\n"
  printf "\n[code around hit]\n"
  x/20i $rip-48
  printf "\n[stack at hit]\n"
  x/32gx $rsp
  printf "\n[frame around rbp]\n"
  x/32gx $rbp-0x80
else
  continue
end
