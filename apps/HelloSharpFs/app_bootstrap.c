extern void __managed__Startup(void);
extern int SharpAppEntry(unsigned long long startupPointer);
int SharpAppBootstrap(unsigned long long startupPointer){ __managed__Startup(); return SharpAppEntry(startupPointer); }
