silent
printf "\n[delayload-zero] DelayLoad_MethodCall entered with corrupt nonvolatiles\n"
printf "delayload_rip=%p module=%p section=%p caller_ra=%p rsp=%p rbp=%p\n", $rip, *((unsigned long long*)$rsp), *((unsigned long long*)($rsp+8)), *((unsigned long long*)($rsp+16)), $rsp, $rbp
printf "rbx=%p rsi=%p rdi=%p r12=%p r13=%p r14=%p r15=%p\n", $rbx,$rsi,$rdi,$r12,$r13,$r14,$r15
printf "rax=%p rcx=%p rdx=%p r8=%p r9=%p r10=%p r11=%p\n", $rax,$rcx,$rdx,$r8,$r9,$r10,$r11
set $caller_ra = *((unsigned long long*)($rsp+16))
set $leaf_return = *((unsigned long long*)($rsp+0x40))
set $fp0 = $rbp
set $ra_fp0 = *((unsigned long long*)($fp0+8))
set $fp1 = *((unsigned long long*)$fp0)
set $ra_fp1 = *((unsigned long long*)($fp1+8))
set $fp2 = *((unsigned long long*)$fp1)
set $ra_fp2 = *((unsigned long long*)($fp2+8))
printf "\n[caller chain]\n"
printf "DelayLoad caller_ra=%p\n", $caller_ra
printf "leaf returns to %p\n", $leaf_return
printf "fp0=%p returns to %p\n", $fp0, $ra_fp0
printf "fp1=%p returns to %p\n", $fp1, $ra_fp1
printf "fp2=%p returns to %p\n", $fp2, $ra_fp2
printf "\n[managed callsite around caller_ra]\n"
x/16i $caller_ra-40
printf "\n[leaf caller around return]\n"
x/16i $leaf_return-40
printf "\n[fp0 caller around return]\n"
x/16i $ra_fp0-40
printf "\n[fp1 caller around return]\n"
x/16i $ra_fp1-40
printf "\n[fp2 caller around return]\n"
x/16i $ra_fp2-40
printf "\n[stack at DelayLoad entry]\n"
x/32gx $rsp
printf "\n[caller frame around rbp]\n"
x/32gx $rbp-0x80
