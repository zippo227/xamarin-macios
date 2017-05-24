#import <Foundation/Foundation.h>

#include <objc/runtime.h>
#include <zlib.h>
#include "libtest.h"

int
theUltimateAnswer ()
{
	return 42;
}

void useZLib ()
{
	printf ("ZLib version: %s\n", zlibVersion ());
}

@interface UltimateMachine : NSObject {

}
- (int) getAnswer;
+ (UltimateMachine *) sharedInstance;
@end

@implementation UltimateMachine
{

}
- (int) getAnswer
{
	return 42;
}

static UltimateMachine *shared;

+ (UltimateMachine *) sharedInstance
{
	if (shared == nil)
		shared = [[UltimateMachine alloc] init];
	return shared;
}
@end

@interface FakeType2 : NSObject {
}
-(BOOL) isKindOfClass: (Class) cls;
@end

@implementation FakeType2
{
}
- (BOOL) isKindOfClass: (Class) cls;
{
	if (cls == objc_getClass ("FakeType1"))
		return YES;
	
	return [super isKindOfClass: cls];
}
@end

/*
 * ObjC test class used for registrar tests.
*/
@implementation ObjCRegistrarTest
{
}
	-(void) V
	{
	}

	-(float) F
	{
		return _Pf1;
	}

	-(double) D
	{
		return _Pd1;
	}

	-(struct Sd) Sd
	{
		return _PSd;
	}

	-(struct Sf) Sf
	{
		return _PSf;
	}

	-(void) V:(int)i1 i:(int)i2 i:(int)i3 i:(int)i4 i:(int)i5 i:(int)i6 i:(int)i7
	{
		// x86_64: 6 in regs, 7th in mem.
		_Pi1 = i1; _Pi2 = i2; _Pi3 = i3; _Pi4 = i4; _Pi5 = i5; _Pi6 = i6; _Pi7 = i7;
	}

	-(void) V:(float)f1 f:(float)f2 f:(float)f3 f:(float)f4 f:(float)f5 f:(float)f6 f:(float)f7 f:(float)f8 f:(float)f9
	{
		// x86_64: 8 in regs, 9th in mem.
		_Pf1 = f1; _Pf2 = f2; _Pf3 = f3; _Pf4 = f4; _Pf5 = f5; _Pf6 = f6; _Pf7 = f7; _Pf8 = f8; _Pf9 = f9;
	}

	-(void) V:(int)i1 i:(int)i2 i:(int)i3 i:(int)i4 i:(int)i5 i:(int)i6 i:(int)i7 f:(float)f1 f:(float)f2 f:(float)f3 f:(float)f4 f:(float)f5 f:(float)f6 f:(float)f7 f:(float)f8 f:(float)f9
	{
		// x86_64: 6 ints in regs, 8 floats in in regs, 1 int in mem, 1 float in mem.
		_Pi1 = i1; _Pi2 = i2; _Pi3 = i3; _Pi4 = i4; _Pi5 = i5; _Pi6 = i6; _Pi7 = i7;
		_Pf1 = f1; _Pf2 = f2; _Pf3 = f3; _Pf4 = f4; _Pf5 = f5; _Pf6 = f6; _Pf7 = f7; _Pf8 = f8; _Pf9 = f9;
	}

	-(void) V:(double)d1 d:(double)d2 d:(double)d3 d:(double)d4 d:(double)d5 d:(double)d6 d:(double)d7 d:(double)d8 d:(double)d9
	{
		// x86_64: 8 in regs, 9th in mem.
		_Pd1 = d1; _Pd2 = d2; _Pd3 = d3; _Pd4 = d4; _Pd5 = d5; _Pd6 = d6; _Pd7 = d7; _Pd8 = d8; _Pd9 = d9;
	}

	-(void) V:(int)i1 i:(int)i2 Siid:(struct Siid)s1 i:(int)i3 i:(int)i4 d:(double)d1 d:(double)d2 d:(double)d3 i:(int)i5 i:(int)i6 i:(int)i7
	{
		_Pi1 = i1; _Pi2 = i2; _PSiid = s1; _Pi3 = i3; _Pi4 = i4; _Pd1 = d1; _Pd2 = d2; _Pd3 = d2; _Pi5 = i5; _Pi6 = i6; _Pi7 = i7;
	}

	-(void) V:(int)i1 i:(int)i2 f:(float)f1 Siid:(struct Siid)s1 i:(int)i3 i:(int)i4 d:(double)d1 d:(double)d2 d:(double)d3 i:(int)i5 i:(int)i6 i:(int)i7
	{
		_Pi1 = i1; _Pi2 = i2; _Pf1 = f1; _PSiid = s1; _Pi3 = i3; _Pi4 = i4; _Pd1 = d1; _Pd2 = d2; _Pd3 = d3; _Pi5 = i5; _Pi6 = i6; _Pi7 = i7;
	}

	-(void) V:(char)c1 c:(char)c2 c:(char)c3 c:(char)c4 c:(char)c5 i:(int)i1 d:(double)d1
	{
		_Pc1 = c1; _Pc2 = c2; _Pc3 = c3; _Pc4 = c4; _Pc5 = c5; _Pi1 = i1; _Pd1 = d1;
	}

	-(void) V:(out id  _Nullable *)n1 n:(out NSString * _Nullable *)n2
	{
		abort (); // this method is supposed to be overridden
	}

	/*
	 * Invoke method
	 */

	-(void) invoke_V
	{
		[self V];
	}

	-(float) invoke_F
	{
		return [self F];
	}

	-(double) invoke_D
	{
		return [self D];
	}

	-(struct Sf) Sf_invoke
	{
		return [self Sf];
	}

	-(void) invoke_V_null_out
	{
		[self V:nil n:nil];
	}

	/*
	 * API returning blocks.
	 */
	-(RegistrarTestBlock) methodReturningBlock
	{
		return nil;
	}

	-(bool) testBlocks
	{
		unsigned int output;
		unsigned int expected;
		unsigned int input;

		input = 0xdeadf00d;
		expected = 0x1337b001;
		output = [self methodReturningBlock] (input);
		if (output != expected) {
			NSLog (@"methodReturningBlock didn't return the expected value 0x%x for the input value 0x%x, but got instead 0x%x.", expected, input, output);
			return false;
		}

		input = 0xdeadf11d;
		expected = 0x7b001133;
		output = self.propertyReturningBlock (input);
		if (output != expected) {
			NSLog (@"propertyReturningBlock didn't return the expected value 0x%x for the input value 0x%x, but got instead 0x%x.", expected, input, output);
			return false;
		}

		return true;
	}

	-(void) idAsIntPtr: (id)p1
	{
		// Nothing to do here.
	}
@end

@implementation ObjCExceptionTest
{
}
	-(void) throwObjCException
	{
		[NSException raise:@"Some exception" format:@"exception was thrown"];
	}

	-(void) throwManagedException
	{
		abort (); // this method should be overridden in managed code.
	}

	-(void) invokeManagedExceptionThrower
	{
		[self throwManagedException];	
	}

	-(void) invokeManagedExceptionThrowerAndRethrow
	{
		@try {
			[self throwManagedException];			
		} @catch (id exc) {
			[NSException raise:@"Caught exception" format:@"exception was rethrown"];
		}
	}
	-(void) invokeManagedExceptionThrowerAndCatch
	{
		@try {
			[self throwManagedException];			
		} @catch (id exc) {
			// do nothing
		}
	}
@end

@interface CtorChaining1 : NSObject
	@property BOOL initCalled;
	@property BOOL initCallsInitCalled;
	-(instancetype) init;
	-(instancetype) initCallsInit:(int) value;
@end
@implementation CtorChaining1
-(instancetype) init
{
	self.initCalled = YES;
	return [super init];
}
-(instancetype) initCallsInit:(int) value
{
	self.initCallsInitCalled = YES;
	return [self init];
}
@end

@implementation ObjCProtocolClassTest
-(void) idAsIntPtr: (id)p1
{
	// Do nothing
}
@end

#include "libtest.decompile.m"
