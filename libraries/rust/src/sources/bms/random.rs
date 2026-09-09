pub struct JavaRandom {
    state: u64,
}

impl JavaRandom {
    const MASK: u64 = (1_u64 << 48) - 1;
    const MULT: u64 = 0x5DEECE66D;
    const ADD: u64 = 0xB;

    pub fn new(seed: i64) -> Self {
        Self {
            state: ((seed as u64) ^ Self::MULT) & Self::MASK,
        }
    }

    fn next(&mut self, bits: u32) -> u64 {
        self.state = (self.state.wrapping_mul(Self::MULT).wrapping_add(Self::ADD)) & Self::MASK;
        self.state >> (48 - bits)
    }

    pub fn next_int(&mut self, bound: u64) -> u64 {
        if bound & bound.wrapping_neg() == bound {
            return (bound.wrapping_mul(self.next(31))) >> 31;
        }
        loop {
            let bits = self.next(31);
            let val = bits % bound;
            if bits.wrapping_sub(val).wrapping_add(bound - 1) <= 0x7FFF_FFFF {
                return val;
            }
        }
    }
}
